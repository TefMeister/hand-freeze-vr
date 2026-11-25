// SpotList.cs -- the hand spots of the gun in hand, as Hand Poser knows them (v6, 2026-11-25). Read only: nothing here writes
// into the game's folder.
//   spot_numbers.json   Hand Poser's numbered spots per gun (L1.., R1..), in <game>\reframework\data\hand_poser
//   state.json          Hand Poser's game script: the gun in hand and the spot the hands are at now
//   guns\*.json         Hand Poser's saved poses: "<gun> @ <left spot> + <right spot>.json"; a spot with a file has its own pose
// Tefa 2026-11-25: "scrap the 3d gun from it and just leave the spots written down ... all the spots on the weapon that can have
// different hand poses saved individually and just have these informative, so they light up - every pose that hand poser has and
// they change with the weapon at hand". The 3D gun, its recipes and its game script are in archive/2026-11-25-spots-3d-gun.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

class Spot {
    public string Hand;      // "l" / "r"
    public string Code;      // L3, R1, H2, A ...
    public string Part;      // the game script's name for this hand's spot
    public string Words;     // the short words on the row
    public bool Saved;       // Hand Poser has a pose saved for this spot (this gun's, or one for every gun)
}

static class GameFiles {
    public const double FRESH_SEC = 3.0;   // state.json older than this: the game script is not answering

    public static bool Looks(string dir) { return dir != null && Directory.Exists(Path.Combine(dir, "reframework")); }
    public static string PoserData(string game) { return Path.Combine(game, "reframework", "data", "hand_poser"); }

    // the running RE game's folder: the game the layer is in, else a known RE game that is running
    public static string Find(int layerPid) {
        if (layerPid != 0) try { string d = Path.GetDirectoryName(Process.GetProcessById(layerPid).MainModule.FileName); if (Looks(d)) return d; } catch (Exception) { }
        foreach (var exe in new[] { "re8", "re2", "re3", "re7" })
            foreach (var p in Process.GetProcessesByName(exe))
                try { string d = Path.GetDirectoryName(p.MainModule.FileName); if (Looks(d)) return d; } catch (Exception) { }
        return null;
    }

    // the gun and the spot the hands are at now; false = the file is missing or stale (gun and spot come back "")
    public static bool Now(string game, out string gun, out string spot) {
        gun = ""; spot = "";
        try {
            string f = Path.Combine(PoserData(game), "state.json");
            if (!File.Exists(f) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > FRESH_SEC) return false;
            var o = Json.Obj(Json.Parse(File.ReadAllText(f))); if (o == null) return false;
            object v;
            if (o.TryGetValue("gun", out v)) gun = Json.Str(v) ?? "";
            if (o.TryGetValue("spot", out v)) spot = Json.Str(v) ?? "";
            return true;
        } catch (Exception) { return false; }
    }
}

static class Spots {
    const string RIGHT = "right hand ";
    // the spots every gun has (and no gun at all): the holsters, the pouch, the map, first aid
    static readonly string[][] FIXED = {
        new[] { "H1", "at right shoulder holster" }, new[] { "H2", "at right hip holster" }, new[] { "H3", "at left hip holster" },
        new[] { "H4", "at left shoulder holster" }, new[] { "A", "ready at pouch" }, new[] { "M", "at map" }, new[] { "F", "at first aid" },
    };

    public static string LeftPart(string spot) { foreach (var p in Parts(spot)) if (!IsRight(p)) return p; return ""; }
    public static string RightPart(string spot) { foreach (var p in Parts(spot)) if (IsRight(p)) return p; return ""; }
    static string[] Parts(string spot) { return (spot ?? "").Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries); }
    static bool IsRight(string p) { return p.StartsWith(RIGHT) || p.StartsWith("right hand,") || p == "trigger pulled"; }

    // every spot of `gun` ("" = no gun in hand: the body's spots only), the left hand's first
    public static List<Spot> List(string game, string gun) {
        var res = new List<Spot>();
        if (game == null) return res;
        gun = gun ?? "";
        Dictionary<string, object> all = null;
        try {
            string f = Path.Combine(GameFiles.PoserData(game), "spot_numbers.json");
            if (File.Exists(f)) all = Json.Obj(Json.Parse(File.ReadAllText(f)));
        } catch (Exception) { }
        object g = null;
        var entry = gun != "" && all != null && all.TryGetValue(gun, out g) ? Json.Obj(g) : null;
        var saved = SavedParts(game, gun);
        foreach (var hand in new[] { "l", "r" }) {
            object l = null;
            var list = entry != null && entry.TryGetValue(hand == "r" ? "R" : "L", out l) ? Json.Arr(l) : null;
            if (list != null) for (int i = 0; i < list.Count; i++) res.Add(Make(hand, (hand == "r" ? "R" : "L") + (i + 1), Json.Str(list[i]), saved));
            foreach (var fx in FIXED) res.Add(Make(hand, fx[0], hand == "r" ? RIGHT + fx[1] : fx[1], saved));
        }
        return res;
    }
    static Spot Make(string hand, string code, string part, HashSet<string> saved) {
        return new Spot { Hand = hand, Code = code, Part = part, Words = Words(part), Saved = saved.Contains(part) };
    }
    static string Words(string part) {
        string p = part.StartsWith("right hand, ") ? part.Substring(12) : part.StartsWith(RIGHT) ? part.Substring(RIGHT.Length) : part;
        if (p.StartsWith("at ")) p = p.Substring(3);
        if (p.EndsWith(" holster")) p = p.Substring(0, p.Length - 8);
        if (p == "ready at pouch") p = "pouch";
        return p;
    }

    // the spots that have a pose of their own in Hand Poser's guns folder: this gun's files, and the ones for every gun
    // ("@ <spot>.json", "any gun @ <spot>.json")
    public static HashSet<string> SavedParts(string game, string gun) {
        var res = new HashSet<string>();
        try {
            string dir = Path.Combine(GameFiles.PoserData(game), "guns");
            if (!Directory.Exists(dir)) return res;
            foreach (var f in Directory.GetFiles(dir, "*.json")) {
                string name = Path.GetFileNameWithoutExtension(f);
                int at = name.IndexOf('@');
                if (at < 0) continue;
                string owner = name.Substring(0, at).Trim(), spot = name.Substring(at + 1).Trim();
                if (owner != "" && owner != "any gun" && owner != gun) continue;
                foreach (var p in Parts(spot)) res.Add(p);
            }
        } catch (Exception) { }
        return res;
    }

    // a gun's name as the window says it: the game's own name where it is known (Tefa 2026-11-25: "the headline that says letters
    // and numbers right now ri3046 for Lemi, needs to say weapon name instead"), else the id without the game's ending.
    // Village's names: visceral-re8-vr's recoil catalogue (dev-archive/native/lua/visceral_re8_menu.lua, REC_CATALOG).
    static readonly Dictionary<string, string> NAMES = new Dictionary<string, string> {
        { "ri3008", "M1851 Wolfsbane" }, { "ri3021", "W870 TAC" }, { "ri3023", "GM 79" }, { "ri3036", "M1897" }, { "ri3040", "M1911" },
        { "ri3042", "F2 Rifle" }, { "ri3046", "LEMI" }, { "ri3047", "V61 Custom" }, { "ri3048", "SYG-12" }, { "ri3049", "WCX" },
        { "ri3050", "S.T.A.K.E" }, { "ri3115", "Rocket Pistol" }, { "ri3116", "Handcannon PZ" }, { "ri3122", "Samurai Edge - AW Model-01" },
        { "ri3124", "USM-AI" }, { "ri3125", "Dragoon" }, { "ri3135", "LEMI - Shadows of Rose" }, { "ri3147", "M1897 - Shadows of Rose" },
        { "ri3081", "LEMI - Mercenaries" }, { "ri3082", "M1911 - Mercenaries" }, { "ri3083", "V61 Custom - Mercenaries" },
        { "ri3084", "M1897 - Mercenaries" }, { "ri3085", "W870 TAC - Mercenaries" }, { "ri3086", "SYG-12 - Mercenaries" },
        { "ri3087", "F2 Rifle - Mercenaries" }, { "ri3129", "Dragoon - Mercenaries" }, { "ri3130", "USM-AI - Mercenaries" },
    };
    public static string GunWords(string gun) {
        string id = (gun ?? "").Replace("_Inventory", "");
        string name; return NAMES.TryGetValue(id, out name) ? name : id;
    }
}
