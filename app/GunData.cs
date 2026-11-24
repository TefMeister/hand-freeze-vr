// GunData.cs -- what the SPOTS tab knows about the gun in hand and its hand spots (2026-11-25).
//   gun_now.json     written by the game script (reframework/autorun/hand_freeze_spots.lua): the gun's joints and grip zones in
//                    the gun's own frame (+x its left, +y up, +z the muzzle), and the gun as the camera sees it
//   guns/<gun>.json  kept by this window beside it: the last picture of each gun, and how the gun sits in the right controller
//                    (learned by itself while the gun is held), so a spot can be picked without learning again
//   Hand Poser's spot_numbers.json (the numbered spots, read only) and state.json (the gun and spot the hands are at now)
// Then the recipes: what the controllers must do for the hand to land on a spot.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

class GunJoint { public string Name = ""; public int Parent = -1; public V3 P; }
class GunZone { public string Key = ""; public double R; public V3 P; }

class GunShape {
    public string Gun = "";
    public List<GunJoint> Joints = new List<GunJoint>();
    public List<GunZone> Zones = new List<GunZone>();
    public bool HasCam; public Pose Cam;            // the gun's root in the camera's frame (live only)
    public bool HasGrip; public Pose Grip;          // the gun's root in the right controller's frame (learned)
    // joints that are not part of the gun's shape (effects hang far off the muzzle)
    public static bool Hidden(string joint) { string n = joint.ToLowerInvariant(); return n.Contains("vfx") || n == "root" || n.Contains("null"); }
    public GunZone Zone(string key) { foreach (var z in Zones) if (z.Key == key) return z; return null; }
    // the middle of everything drawn, to turn the picture about
    public V3 Middle {
        get {
            if (Joints.Count == 0 && Zones.Count == 0) return V3.Zero;
            double x0 = 1e9, y0 = 1e9, z0 = 1e9, x1 = -1e9, y1 = -1e9, z1 = -1e9;
            Action<V3> take = p => { x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y); z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z); };
            foreach (var j in Joints) if (!Hidden(j.Name)) take(j.P);
            foreach (var z in Zones) take(z.P);
            return new V3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
        }
    }
    public double Reach {   // the farthest drawn thing from the middle (metres), for the picture's size
        get {
            V3 m = Middle; double r = 0.08;
            foreach (var j in Joints) if (!Hidden(j.Name)) r = Math.Max(r, V3.Dist(j.P, m));
            foreach (var z in Zones) r = Math.Max(r, V3.Dist(z.P, m));
            return r;
        }
    }
}

class Spot {
    public string Hand;      // "l" / "r"
    public string Code;      // L3, R1, H2, A ...
    public string Part;      // the game script's name for this hand's spot
    public string Words;     // the short words on the button
    public string ZoneKey;   // the grip zone it lives at, or null
    public bool CanDo;       // a recipe exists (and the zone is on this gun)
}

// one moment of a recipe: where the left controller is (a zone plus an offset in the gun's frame, or a place by the body) and
// what is pressed, held for Ms before the next
class Step {
    public string Zone; public V3 Off; public bool Park, Pouch;
    public double LGrip, LTrig, RTrig; public bool RHigh;
    public int Ms = 400;
}

static class GunFiles {
    public const double FRESH_SEC = 2.0;

    public static string Find(int layerPid) {
        if (layerPid != 0) try { string d = Path.GetDirectoryName(Process.GetProcessById(layerPid).MainModule.FileName); if (Looks(d)) return d; } catch (Exception) { }
        foreach (var exe in new[] { "re8", "re2", "re3", "re7" })
            foreach (var p in Process.GetProcessesByName(exe))
                try { string d = Path.GetDirectoryName(p.MainModule.FileName); if (Looks(d)) return d; } catch (Exception) { }
        return null;
    }
    // with no game running: Steam's usual places (for the PUT THE SCRIPT INTO THE GAME button)
    public static string Installed() {
        foreach (var root in new[] { @"C:\Steam", @"C:\Program Files (x86)\Steam", @"D:\Steam", @"D:\SteamLibrary", @"E:\SteamLibrary" })
            foreach (var name in new[] { "Resident Evil Village BIOHAZARD VILLAGE", "RESIDENT EVIL 2  BIOHAZARD RE2", "RE3" }) {
                string d = Path.Combine(root, "steamapps", "common", name);
                if (Looks(d)) return d;
            }
        return null;
    }
    public static bool Looks(string dir) { return dir != null && Directory.Exists(Path.Combine(dir, "reframework")); }
    public static string Data(string game) { return Path.Combine(game, "reframework", "data", "hand_freeze"); }
    public static string PoserData(string game) { return Path.Combine(game, "reframework", "data", "hand_poser"); }

    static V3 Vec(object o) { var a = Json.Arr(o); return a == null || a.Count < 3 ? V3.Zero : new V3(Json.Num(a[0], 0), Json.Num(a[1], 0), Json.Num(a[2], 0)); }
    static List<object> Vec(V3 v) { return new List<object> { v.X, v.Y, v.Z }; }
    static bool PoseFrom(object o, out Pose p) {
        p = Pose.One;
        var d = Json.Obj(o); if (d == null) return false;
        object pp, qq; if (!d.TryGetValue("p", out pp) || !d.TryGetValue("q", out qq)) return false;
        var a = Json.Arr(qq); if (a == null || a.Count < 4) return false;
        p = new Pose(new Quat(Json.Num(a[0], 0), Json.Num(a[1], 0), Json.Num(a[2], 0), Json.Num(a[3], 1)).Unit, Vec(pp));
        return true;
    }
    static Dictionary<string, object> PoseTo(Pose p) {
        return new Dictionary<string, object> { { "p", Vec(p.P) }, { "q", new List<object> { p.Q.X, p.Q.Y, p.Q.Z, p.Q.W } } };
    }

    public static GunShape Parse(string text) {
        var o = Json.Obj(Json.Parse(text)); if (o == null) return null;
        object v;
        var g = new GunShape { Gun = o.TryGetValue("gun", out v) ? Json.Str(v) : "" };
        if (o.TryGetValue("joints", out v) && Json.Arr(v) != null)
            foreach (var item in Json.Arr(v)) {
                var j = Json.Obj(item); if (j == null) continue;
                object n, par, p; j.TryGetValue("n", out n); j.TryGetValue("par", out par); j.TryGetValue("p", out p);
                g.Joints.Add(new GunJoint { Name = Json.Str(n), Parent = (int)Json.Num(par, -1), P = Vec(p) });
            }
        if (o.TryGetValue("zones", out v) && Json.Arr(v) != null)
            foreach (var item in Json.Arr(v)) {
                var z = Json.Obj(item); if (z == null) continue;
                object k, r, p; z.TryGetValue("key", out k); z.TryGetValue("r", out r); z.TryGetValue("p", out p);
                g.Zones.Add(new GunZone { Key = Json.Str(k), R = Json.Num(r, 0.1), P = Vec(p) });
            }
        if (o.TryGetValue("cam", out v)) g.HasCam = PoseFrom(v, out g.Cam);
        if (o.TryGetValue("grip", out v)) g.HasGrip = PoseFrom(v, out g.Grip);
        return g;
    }
    public static string Write(GunShape g) {
        var joints = new List<object>(); var zones = new List<object>();
        foreach (var j in g.Joints) joints.Add(new Dictionary<string, object> { { "n", j.Name }, { "par", (double)j.Parent }, { "p", Vec(j.P) } });
        foreach (var z in g.Zones) zones.Add(new Dictionary<string, object> { { "key", z.Key }, { "r", z.R }, { "p", Vec(z.P) } });
        var o = new Dictionary<string, object> { { "gun", g.Gun }, { "joints", joints }, { "zones", zones } };
        if (g.HasGrip) o["grip"] = PoseTo(g.Grip);
        return Json.Write(o);
    }

    // the game script's file, only while it is being written (null = no game, no gun, or caught mid-write)
    public static GunShape Live(string game) {
        try {
            string f = Path.Combine(Data(game), "gun_now.json");
            if (!File.Exists(f) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > FRESH_SEC) return null;
            var g = Parse(File.ReadAllText(f));
            return g == null || g.Gun == "" ? null : g;
        } catch (Exception) { return null; }
    }
    static string Safe(string gun) { foreach (char c in Path.GetInvalidFileNameChars()) gun = gun.Replace(c, '_'); return gun; }
    public static string KeptPath(string game, string gun) { return Path.Combine(Data(game), "guns", Safe(gun) + ".json"); }
    public static GunShape Kept(string game, string gun) {
        try { string f = KeptPath(game, gun); return File.Exists(f) ? Parse(File.ReadAllText(f)) : null; } catch (Exception) { return null; }
    }
    public static void Keep(string game, GunShape g) {
        try {
            string f = KeptPath(game, g.Gun);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            string tmp = f + ".tmp";
            File.WriteAllText(tmp, Write(g));
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    // the gun kept last (for a window opened with no game running)
    public static GunShape Newest(string game) {
        try {
            string dir = Path.Combine(Data(game), "guns");
            if (!Directory.Exists(dir)) return null;
            string best = null; DateTime t = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(dir, "*.json")) { var w = File.GetLastWriteTimeUtc(f); if (w > t) { t = w; best = f; } }
            return best == null ? null : Parse(File.ReadAllText(best));
        } catch (Exception) { return null; }
    }

    // Hand Poser's state.json: the gun and the spot the hands are at now (spot "" when it cannot be read)
    public static void Now(string game, out string gun, out string spot) {
        gun = ""; spot = "";
        try {
            string f = Path.Combine(PoserData(game), "state.json");
            if (!File.Exists(f) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 3.0) return;
            var o = Json.Obj(Json.Parse(File.ReadAllText(f))); if (o == null) return;
            object v;
            if (o.TryGetValue("gun", out v)) gun = Json.Str(v);
            if (o.TryGetValue("spot", out v)) spot = Json.Str(v);
        } catch (Exception) { }
    }
}

// the numbered spots (Hand Poser's SpotNumbers.cs keeps the file; here it is only read) and what each needs from the controllers
static class Spots {
    const string RIGHT = "right hand ";
    static readonly string[][] FIXED = {
        new[] { "H1", "at right shoulder holster" }, new[] { "H2", "at right hip holster" }, new[] { "H3", "at left hip holster" },
        new[] { "H4", "at left shoulder holster" }, new[] { "A", "ready at pouch" }, new[] { "M", "at map" }, new[] { "F", "at first aid" },
    };
    // a spot's words -> the grip zone's key in zones.json
    static readonly Dictionary<string, string> ZONE_OF = new Dictionary<string, string> {
        { "slide", "slide" }, { "slide from the left", "slide-left" }, { "magazine", "mag" }, { "well", "well" }, { "handle", "handle" },
        { "drum", "drum" }, { "insert point", "insert" }, { "front grip", "grip" }, { "pump", "pump" }, { "bolt", "bolt" },
    };
    public const double BACK_M = 0.05;        // a slide taken to the back: this far against the muzzle
    public const double MAG_OUT_M = 0.14;     // a magazine pulled clear: this far down the grip
    public const double MAG_PART_M = 0.045;   // ... half way out

    public static string LeftPart(string spot) { foreach (var p in Parts(spot)) if (!IsRight(p)) return p; return ""; }
    public static string RightPart(string spot) { foreach (var p in Parts(spot)) if (IsRight(p)) return p; return ""; }
    static string[] Parts(string spot) { return (spot ?? "").Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries); }
    static bool IsRight(string p) { return p.StartsWith(RIGHT) || p.StartsWith("right hand,") || p == "trigger pulled"; }

    public static List<Spot> List(string game, GunShape gun) {
        var res = new List<Spot>();
        if (game == null || gun == null || gun.Gun == "") return res;
        Dictionary<string, object> all = null;
        try {
            string f = Path.Combine(GunFiles.PoserData(game), "spot_numbers.json");
            if (File.Exists(f)) all = Json.Obj(Json.Parse(File.ReadAllText(f)));
        } catch (Exception) { }
        object g = null;
        var entry = all != null && all.TryGetValue(gun.Gun, out g) ? Json.Obj(g) : null;
        foreach (var hand in new[] { "l", "r" }) {
            object l = null;
            var list = entry != null && entry.TryGetValue(hand == "r" ? "R" : "L", out l) ? Json.Arr(l) : null;
            if (list != null) for (int i = 0; i < list.Count; i++) res.Add(Make(hand, (hand == "r" ? "R" : "L") + (i + 1), Json.Str(list[i]), gun));
            foreach (var fx in FIXED) res.Add(Make(hand, fx[0], hand == "r" ? RIGHT + fx[1] : fx[1], gun));
        }
        return res;
    }
    static Spot Make(string hand, string code, string part, GunShape gun) {
        var s = new Spot { Hand = hand, Code = code, Part = part, Words = Words(part) };
        s.ZoneKey = ZoneKey(hand, part);
        s.CanDo = Recipe(s, gun) != null;
        return s;
    }
    static string Words(string part) {
        string p = part.StartsWith("right hand, ") ? part.Substring(12) : part.StartsWith(RIGHT) ? part.Substring(RIGHT.Length) : part;
        if (p.StartsWith("at ")) p = p.Substring(3);
        if (p.EndsWith(" holster")) p = p.Substring(0, p.Length - 8);
        if (p == "ready at pouch") p = "pouch";
        return p;
    }
    static string Bare(string part) {   // "ready at slide" / "ready on slide" / "slide at the back" -> "slide"
        string p = part;
        if (p.StartsWith("ready at ")) p = p.Substring(9); else if (p.StartsWith("ready on ")) p = p.Substring(9);
        if (p.EndsWith(" at the back")) p = p.Substring(0, p.Length - 12);
        return p;
    }
    static string ZoneKey(string hand, string part) {
        if (hand != "l") return null;
        if (part.Contains("magazine") || part.StartsWith("spare in hand")) return "mag";
        string z; return ZONE_OF.TryGetValue(Bare(part), out z) ? z : null;
    }

    // What the controllers do, step by step, so the game puts the hand on the spot. null = not in this version (a body spot, a
    // spot whose zone this gun does not have, a right-hand spot other than the trigger). The left-hand steps follow the robot
    // tools that already do this on the simulator (visceral-re8-vr tools/sim: walk.py, hold_at.py).
    public static List<Step> Recipe(Spot s, GunShape gun) {
        var r = new List<Step>();
        if (s.Hand == "r") {
            if (s.Part != "trigger pulled") return null;
            r.Add(new Step { Park = true, Ms = 300 });
            r.Add(new Step { Park = true, RTrig = 1 });
            return r;
        }
        string z = s.ZoneKey, p = s.Part;
        if (z == null || gun == null || gun.Zone(z) == null) return null;
        if (p.StartsWith("old magazine in palm")) return null;   // a moment of the reload, not a place the hand can be put yet
        V3 down = new V3(0, -1, 0), back = new V3(0, 0, -1);
        r.Add(new Step { Park = true, Ms = 500 });                       // let go of whatever the last spot left in the hand
        bool spare = p.StartsWith("spare in hand") || p == "two magazines in hand";
        if (spare) {   // a spare magazine from the pouch first
            r.Add(new Step { Pouch = true, Ms = 800 });
            r.Add(new Step { Pouch = true, LGrip = 1, Ms = 800 });
            if (p == "spare in hand, magazine still in the gun") { r.Add(new Step { Zone = z, Off = down * MAG_OUT_M, LGrip = 1 }); return r; }
            r.Add(new Step { Zone = z, LGrip = 1, Ms = 900 });
            if (p == "spare in hand, docked on the magazine") return r;
            r.Add(new Step { Zone = z, LGrip = 1, RHigh = true, Ms = 400 });
            r.Add(new Step { Zone = z, LGrip = 1, Ms = 600 });
            r.Add(new Step { Zone = z, Off = down * (p == "two magazines in hand" ? MAG_OUT_M : MAG_PART_M), LGrip = 1 });
            return r;
        }
        r.Add(new Step { Zone = z, Ms = 500 });
        if (p.StartsWith("ready at ")) return r;
        r.Add(new Step { Zone = z, LGrip = 1, Ms = 500 });
        if (p.StartsWith("ready on ")) return r;
        if (p == "magazine in hand") {   // B lets the magazine go into the hand, then it is pulled clear
            r.Add(new Step { Zone = z, LGrip = 1, RHigh = true, Ms = 400 });
            r.Add(new Step { Zone = z, LGrip = 1, Ms = 600 });
            r.Add(new Step { Zone = z, Off = down * MAG_OUT_M, LGrip = 1 });
            return r;
        }
        r.Add(new Step { Zone = z, LGrip = 1, LTrig = 1, Ms = 500 });
        if (p.EndsWith(" at the back")) r.Add(new Step { Zone = z, Off = back * BACK_M, LGrip = 1, LTrig = 1 });
        return r;
    }
}
