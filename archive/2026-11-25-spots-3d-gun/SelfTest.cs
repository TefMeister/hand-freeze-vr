// SelfTest.cs -- `HandFreeze.exe --selftest`: the SPOTS tab's sums and recipes, with no game and no window (2026-11-25).
// Writes selftest.txt beside the exe's working folder and returns 0 when every check passes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

static class SelfTest {
    static int failed; static readonly StringBuilder log = new StringBuilder();
    static void Check(string what, bool ok) { log.AppendLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failed++; }
    static bool Near(V3 a, V3 b, double m = 1e-6) { return V3.Dist(a, b) < m; }

    static GunShape Pistol() {
        var g = new GunShape { Gun = "test_pistol" };
        g.Joints.Add(new GunJoint { Name = "body", Parent = -1, P = new V3(0, 0, 0) });
        g.Joints.Add(new GunJoint { Name = "muzzle", Parent = 0, P = new V3(0, 0.03, 0.16) });
        g.Joints.Add(new GunJoint { Name = "mag", Parent = 0, P = new V3(0, -0.11, -0.02) });
        g.Zones.Add(new GunZone { Key = "slide", R = 0.12, P = new V3(-0.04, 0.07, 0.05) });
        g.Zones.Add(new GunZone { Key = "mag", R = 0.11, P = new V3(0.06, -0.09, -0.01) });
        g.HasGrip = true;
        g.Grip = new Pose((Quat.About(new V3(1, 0, 0), 35) * Quat.About(new V3(0, 1, 0), 180)).Unit, new V3(0.01, 0.04, -0.03));
        return g;
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

        // the gun in the right controller, learned from the layer's and the game script's numbers
        var gun = Pistol();
        var toldRight = new Pose(Quat.About(new V3(0.2, 1, 0.1), 70), new V3(0.35, 1.2, -0.3));
        var gunInCamera = head.Inverse * toldRight * gun.Grip;
        var learned = SpotEngine.GripFrom(head, toldRight, gunInCamera);
        Check("the gun's seat in the controller comes back from head, controller and camera view", Near(learned.P, gun.Grip.P) && Quat.Apart(learned.Q, gun.Grip.Q) < 1e-6);

        // a spot: its zone lands on the anchor, the gun is turned as the picture, the left controller sits in the zone
        V3 anchor = level.Apply(new V3(0, -0.03, -0.45));
        foreach (var view in GunView.Sides) {
            Pose l, r; bool rides;
            var zone = gun.Zone("slide");
            SpotEngine.Targets(level, view, anchor, gun, zone.P, new Step { Zone = "slide" }, out l, out r, out rides);
            var gunPose = r * gun.Grip;
            Check("the slide zone sits on the anchor", Near(gunPose.Apply(zone.P), anchor) && Near(l.P, anchor) && rides);
            Check("the gun is turned as the picture, seen from the level frame", Quat.Apart(level.Q.Inverse * gunPose.Q, view) < 1e-3);
        }
        {
            Pose l, r; bool rides;
            SpotEngine.Targets(level, GunView.Sides[0], anchor, gun, gun.Zone("slide").P, new Step { Zone = "slide", Off = new V3(0, 0, -Spots.BACK_M) }, out l, out r, out rides);
            var gunPose = r * gun.Grip;
            Check("a slide at the back: the left controller 5 cm against the muzzle", Near(gunPose.Inverse.Apply(l.P), gun.Zone("slide").P + new V3(0, 0, -0.05)));
            Check("the left side view: the muzzle points to the level frame's left", Near(level.Q.Inverse.Turn(gunPose.Q.Turn(new V3(0, 0, 1))), new V3(-1, 0, 0)));
            SpotEngine.Targets(level, GunView.Sides[0], anchor, gun, gun.Zone("mag").P, new Step { Pouch = true }, out l, out r, out rides);
            Check("the pouch is by the body and does not ride with the carry", !rides && l.P.Y < head.P.Y - 0.5);
        }

        // recipes
        Func<string, string, Spot> spot = (hand, part) => new Spot { Hand = hand, Part = part, Code = "T", ZoneKey = part.Contains("magazine") || part.StartsWith("spare") ? "mag" : part.Contains("slide") ? "slide" : null };
        var ready = Spots.Recipe(spot("l", "ready at slide"), gun); var on = Spots.Recipe(spot("l", "ready on slide"), gun);
        var held = Spots.Recipe(spot("l", "slide"), gun); var back = Spots.Recipe(spot("l", "slide at the back"), gun);
        Check("ready at: in the zone, nothing pressed", ready != null && Last(ready).Zone == "slide" && Last(ready).LGrip == 0 && Last(ready).LTrig == 0);
        Check("ready on: the grip held", on != null && Last(on).LGrip == 1 && Last(on).LTrig == 0);
        Check("on the slide: grip and trigger", held != null && Last(held).LGrip == 1 && Last(held).LTrig == 1 && Last(held).Off.Length == 0);
        Check("at the back: pulled against the muzzle", back != null && Last(back).Off.Z < -0.04 && Last(back).LTrig == 1);
        var magOut = Spots.Recipe(spot("l", "magazine in hand"), gun);
        Check("magazine in hand: B pressed on the way, then pulled down", magOut != null && magOut.Exists(s => s.RHigh) && Last(magOut).Off.Y < -0.1 && !Last(magOut).RHigh);
        var two = Spots.Recipe(spot("l", "two magazines in hand"), gun);
        Check("two magazines: the pouch first", two != null && two.Exists(s => s.Pouch && s.LGrip == 1) && Last(two).Off.Y < -0.1);
        Check("every recipe starts by letting go", ready[0].Park && two[0].Park && ready[0].LGrip == 0);
        Check("the trigger: the right trigger held", Last(Spots.Recipe(spot("r", "trigger pulled"), gun)).RTrig == 1);
        Check("a holster has no recipe yet", Spots.Recipe(new Spot { Hand = "l", Part = "at right hip holster" }, gun) == null);
        var noSlide = Pistol(); noSlide.Zones.RemoveAt(0);
        Check("a gun without that zone: no recipe", Spots.Recipe(spot("l", "slide"), noSlide) == null);

        // the carry's sticks
        var g0 = Pose.One; V3 mid = new V3(0, 1.5, -0.5);
        var turned = SpotEngine.StickMove(g0, level, mid, 1, 0, 0, 0.5);
        Check("the right stick turns about the rig's middle (the middle stays)", Near(turned.Apply(mid), mid) && Math.Abs(Quat.Apart(turned.Q, Quat.One) - 30) < 1e-6);
        var pushed = SpotEngine.StickMove(g0, level, mid, 0, 0, 1, 0.5);
        Check("the left stick pushes it away, level", Near(pushed.Apply(mid), mid + level.Q.Turn(new V3(0, 0, -1)) * 0.25));
        Check("a resting stick moves nothing", Near(SpotEngine.StickMove(g0, level, mid, 0.1, -0.1, 0.05, 1).Apply(mid), mid));

        // the kept gun file round trip
        var again = GunFiles.Parse(GunFiles.Write(gun));
        Check("a kept gun reads back", again.Gun == gun.Gun && again.Joints.Count == 3 && again.Zones.Count == 2 && again.HasGrip && Near(again.Grip.P, gun.Grip.P) && Near(again.Zone("mag").P, gun.Zone("mag").P));
        Check("spot words split per hand", Spots.LeftPart("slide + right hand on the cover") == "slide" && Spots.RightPart("free + trigger pulled") == "trigger pulled" && Spots.LeftPart("right hand at map") == "");

        log.AppendLine(failed == 0 ? "ALL PASS" : failed + " FAILED");
        try { File.WriteAllText("selftest.txt", log.ToString()); } catch (Exception) { }
        return failed == 0 ? 0 : 1;
    }
    static Step Last(List<Step> r) { return r[r.Count - 1]; }
}
