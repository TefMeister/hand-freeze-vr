// SelfTest.cs -- `HandFreeze.exe --selftest`: the sums, the carry's sticks and the spot list, with no game and no window.
// Writes selftest.txt in the working folder and returns 0 when every check passes. The spot list is read from a pretend game
// folder made in the temp folder (never a real game's).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

static class SelfTest {
    static int failed; static readonly StringBuilder log = new StringBuilder();
    static void Check(string what, bool ok) { log.AppendLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failed++; }
    static bool Near(V3 a, V3 b, double m = 1e-6) { return V3.Dist(a, b) < m; }

    // a pretend game: Hand Poser's files for a pistol with a few spots and a few saved poses
    public static string PretendGame(string gun, string spot) {
        string game = Path.Combine(Path.GetTempPath(), "handfreeze_pretend_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string data = GameFiles.PoserData(game), guns = Path.Combine(data, "guns");
        Directory.CreateDirectory(guns);
        File.WriteAllText(Path.Combine(data, "spot_numbers.json"),
            "{\"ri3046_Inventory\":{\"L\":[\"ready at slide\",\"ready on slide\",\"slide\",\"slide at the back\",\"ready at slide from the left\",\"ready on slide from the left\",\"slide from the left\",\"slide from the left at the back\",\"ready at magazine\",\"ready on magazine\",\"magazine\",\"magazine in hand\",\"two magazines in hand\",\"spare in hand, docked on the magazine\",\"spare in hand, pulling the magazine out\",\"spare in hand, magazine still in the gun\",\"ready at well\"],\"R\":[\"trigger pulled\"]},\"ri3008_Inventory\":{\"L\":[\"ready at handle\",\"handle\"],\"R\":[\"right hand on the cover\"]}}");
        foreach (var f in new[] { "ri3046_Inventory @ slide.json", "ri3046_Inventory @ magazine in hand + trigger pulled.json", "ri3008_Inventory @ handle.json",
                                  "@ free + right hand at right hip holster.json", "any gun @ ready at pouch.json", "ri3046_Inventory.json" })
            File.WriteAllText(Path.Combine(guns, f), "{}");
        File.WriteAllText(Path.Combine(data, "state.json"), "{\"gun\":\"" + gun + "\",\"spot\":\"" + spot + "\"}");
        return game;
    }

    public static int Run() {
        // turns and poses
        var q = Quat.About(new V3(0, 1, 0), 90);
        Check("a quarter turn about up takes ahead (-z) to the left (-x)", Near(q.Turn(new V3(0, 0, -1)), new V3(-1, 0, 0)));
        var a = new Pose(Quat.About(new V3(1, 2, 3), 40), new V3(0.3, -1, 2)); var b = new Pose(Quat.About(new V3(-2, 0.5, 1), 110), new V3(-4, 0.2, 0.1));
        Check("a pose times its inverse is nothing", Near((a * a.Inverse).P, V3.Zero) && Quat.Apart((a * a.Inverse).Q, Quat.One) < 1e-6);
        Check("poses chain like their points", Near((a * b).Apply(new V3(1, 1, 1)), a.Apply(b.Apply(new V3(1, 1, 1)))));
        var head = new Pose((Quat.About(new V3(0, 1, 0), 30) * Quat.About(new V3(1, 0, 0), -25)).Unit, new V3(0.2, 1.6, 0.1));
        var level = Pose.Level(head);
        Check("the level frame keeps the head's place and its left / right turn only", Near(level.P, head.P) && Math.Abs(level.Q.Turn(new V3(0, 0, -1)).Y) < 1e-9 && Quat.Apart(level.Q, Quat.About(new V3(0, 1, 0), 30)) < 1e-6);

        // the carry's sticks
        var g0 = Pose.One; V3 mid = new V3(0, 1.5, -0.5);
        var turned = HandCarry.StickMove(g0, level, mid, 1, 0, 0, 0.5);
        Check("the right stick turns about the rig's middle (the middle stays)", Near(turned.Apply(mid), mid) && Math.Abs(Quat.Apart(turned.Q, Quat.One) - 30) < 1e-6);
        var pushed = HandCarry.StickMove(g0, level, mid, 0, 0, 1, 0.5);
        Check("the left stick pushes it away, level", Near(pushed.Apply(mid), mid + level.Q.Turn(new V3(0, 0, -1)) * 0.25));
        Check("a resting stick moves nothing", Near(HandCarry.StickMove(g0, level, mid, 0.1, -0.1, 0.05, 1).Apply(mid), mid));

        // the spots
        Check("spot words split per hand", Spots.LeftPart("slide + right hand on the cover") == "slide" && Spots.RightPart("free + trigger pulled") == "trigger pulled" && Spots.LeftPart("right hand at map") == "");
        string game = PretendGame("ri3046_Inventory", "slide + trigger pulled");
        try {
            var list = Spots.List(game, "ri3046_Inventory");
            Func<string, string, Spot> find = (hand, code) => list.Find(s => s.Hand == hand && s.Code == code);
            Check("the pistol: 17 numbered + 7 body spots left, 1 + 7 right", list.FindAll(s => s.Hand == "l").Count == 24 && list.FindAll(s => s.Hand == "r").Count == 8);
            Check("L3 is the slide, R1 the trigger, the left hand's first", find("l", "L3") != null && find("l", "L3").Part == "slide" && find("r", "R1").Part == "trigger pulled" && list[0].Hand == "l");
            Check("a spot with a file of this gun's is saved", find("l", "L3").Saved && find("l", "L12").Saved && find("r", "R1").Saved);
            Check("a spot with no file is not", !find("l", "L1").Saved && !find("l", "L17").Saved);
            Check("a file for every gun counts for this gun too", find("l", "A").Saved && find("r", "H2").Saved);
            Check("another gun's file does not", Spots.List(game, "ri3046_Inventory").Find(s => s.Part == "handle") == null && !Spots.SavedParts(game, "ri3046_Inventory").Contains("handle"));
            Check("the short words: holsters and the pouch", find("l", "H3").Words == "left hip" && find("l", "A").Words == "pouch" && find("r", "H1").Words == "right shoulder");
            var other = Spots.List(game, "ri3008_Inventory");
            Check("another gun, another list", other.FindAll(s => s.Hand == "l").Count == 9 && other.Find(s => s.Code == "L2").Saved);
            var none = Spots.List(game, "");
            Check("no gun in hand: the body's spots only", none.Count == 14 && none.Find(s => s.Code == "L1") == null);
            Check("a gun Hand Poser has no numbers for: the body's spots only", Spots.List(game, "unknown_gun").Count == 14);
            string gun, spot;
            Check("the game says the gun and the spot", GameFiles.Now(game, out gun, out spot) && gun == "ri3046_Inventory" && Spots.LeftPart(spot) == "slide" && Spots.RightPart(spot) == "trigger pulled");
            File.SetLastWriteTimeUtc(Path.Combine(GameFiles.PoserData(game), "state.json"), DateTime.UtcNow.AddSeconds(-30));
            Check("an old state file: the game is not answering", !GameFiles.Now(game, out gun, out spot) && gun == "");
            Check("the gun's name without the game's ending", Spots.GunWords("ri3046_Inventory") == "ri3046");
        } finally { try { Directory.Delete(game, true); } catch (Exception) { } }

        log.AppendLine(failed == 0 ? "ALL PASS" : failed + " FAILED");
        try { File.WriteAllText("selftest.txt", log.ToString()); } catch (Exception) { }
        return failed == 0 ? 0 : 1;
    }
}
