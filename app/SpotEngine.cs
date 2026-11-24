// SpotEngine.cs -- puts the frozen hands on a picked spot, and lets Window View's grips carry them (2026-11-25).
//
// Tefa: "when clicked on one of these, while hand freeze is active, both hands with the weapon get raised to my eye height in
// front of me already holding this position that the tab i just clicked on describes, and places the weapon at the angle the 3d
// gun in the hand freeze app is rotated to"; then "to be able to move and rotate the frozen weapon with hands on it in the same
// way [as Window View's windows] without unfreezing them - while i got the pointers out".
//
// The sums, all in the layer's room:
//   the level frame   the head when the spot was picked, turned only left / right (x right, y up, -z ahead)
//   the anchor        DISTANCE ahead of it at the eyes' height (+ HEIGHT): the spot's grip zone is put exactly there
//   the gun           turned as the picture is turned, as seen from the level frame
//   the right hand    the gun x (the gun in the right controller, learned)^-1
//   the left hand     the spot's zone on that gun (+ the recipe's offset), with the recipe's grip and trigger
//   the carry         one rigid move G over both hands; a held grip (pointer off every window) drags G with the real controller,
//                     the right stick turns / tilts it, the left stick pushes it away / pulls it closer
// "The gun in the right controller" is learned by itself while a gun is held and the head is still: the layer says where the game
// is told the controller is and where the head is, the game script says where the camera sees the gun.
using System;
using System.Collections.Generic;

class SpotEngine : IDisposable {
    public const int DISTANCE_CM = 45, HEIGHT_CM = -3;      // the anchor: this far ahead, this much above the eyes
    const double GRAB_AT = 0.7, LET_GO_AT = 0.3;            // the grip's travel that takes hold / lets go
    const double TURN_DEG_PER_S = 60, PUSH_M_PER_S = 0.5;   // the sticks while carrying (Window View's own rates)
    const double LEARN_CM = 1.0, LEARN_DEG = 2.0; const int LEARN_SAMPLES = 4;
    static readonly V3 PARK = new V3(-0.25, -0.30, 0.10);   // the left hand out of the way: left of, below and nearer than the anchor
    static readonly V3 POUCH = new V3(-0.20, -0.62, -0.02); // the ammo pouch from the head (the robot tools' number)
    static readonly Quat ALONG_GUN = new Quat(0, 1, 0, 0);  // the left controller pointing the muzzle's way

    public readonly Shared Freeze;
    readonly PoseShared pose = new PoseShared();
    readonly WindowViewLook wv = new WindowViewLook();

    public string ForcedGame;   // tests: a pretend game's folder
    int findTick;
    public string Game; public GunShape Gun; public List<Spot> SpotList = new List<Spot>();
    public string RealLeft = "", RealRight = "";
    public Spot Picked; public string Note = "";
    public Quat View = GunView.Sides[0];
    public int DistanceCm = DISTANCE_CM, HeightCm = HEIGHT_CM;
    public bool Carrying { get { return carryHand != 0; } }
    public string PickedCode { get { return Picked == null ? "" : Picked.Hand + Picked.Code; } }

    bool haveLevel; Pose level; Pose G = Pose.One;
    List<Step> recipe; int stepAt; DateTime stepStart;
    bool plain; int plainMask; Pose plainL, plainR;
    int carryHand; Pose carryStart, carryG0; bool gripWasL, gripWasR;
    readonly List<Pose> learnSeen = new List<Pose>();
    string keptStamp = "";

    public SpotEngine(Shared freeze) { Freeze = freeze; }
    public void Dispose() { pose.Dispose(); wv.Dispose(); }
    public bool Posing { get { return Picked != null || plain; } }

    // ---- four times a second: the game, the gun, the spots ---------------------------------------------------------------------
    public void Slow() {
        string found = ForcedGame ?? (Game == null || findTick++ % 8 == 0 ? GunFiles.Find(Freeze.GameConnected ? Freeze.LayerPid : 0) : null);
        if (found != null) Game = found;
        if (Game == null) return;
        var live = GunFiles.Live(Game);
        if (live != null) {
            if (Gun != null && Gun.Gun == live.Gun) { live.HasGrip = Gun.HasGrip; live.Grip = Gun.Grip; }
            else { var kept = GunFiles.Kept(Game, live.Gun); if (kept != null) { live.HasGrip = kept.HasGrip; live.Grip = kept.Grip; } learnSeen.Clear(); if (Picked != null) LetGoSpot(); }
            Gun = live;
            Learn();
            string stamp = GunFiles.Write(Gun);
            if (stamp != keptStamp) { keptStamp = stamp; GunFiles.Keep(Game, Gun); }
        } else if (Gun == null) Gun = GunFiles.Newest(Game);
        SpotList = Spots.List(Game, Gun);
        string gunNow, spot; GunFiles.Now(Game, out gunNow, out spot);
        RealLeft = Spots.LeftPart(spot); RealRight = Spots.RightPart(spot);
        if (Freeze.Frozen == 0 && Posing) Reset();
    }

    // how the gun sits in the right controller, from where the game is told the controller is, the head, and the gun in the camera
    public static Pose GripFrom(Pose head, Pose toldRight, Pose gunInCamera) { return toldRight.Inverse * head * gunInCamera; }

    void Learn() {
        if (Posing || carryHand != 0 || Gun == null || !Gun.HasCam || !pose.Live(PoseShared.LIVE_HEAD) || !pose.Live(PoseShared.LIVE_RIGHT)) { learnSeen.Clear(); return; }
        if (RealLeft != "" && RealLeft != "free" && !RealLeft.StartsWith("at ")) { learnSeen.Clear(); return; }   // a left hand on the gun may be steering it
        Pose now = GripFrom(pose.Head, pose.Told(Hands.RIGHT), Gun.Cam);
        learnSeen.Add(now);
        if (learnSeen.Count > LEARN_SAMPLES) learnSeen.RemoveAt(0);
        if (learnSeen.Count < LEARN_SAMPLES) return;
        foreach (var p in learnSeen) if (V3.Dist(p.P, now.P) * 100 > LEARN_CM || Quat.Apart(p.Q, now.Q) > LEARN_DEG) return;   // the head or the hand was moving
        if (Gun.HasGrip && V3.Dist(Gun.Grip.P, now.P) * 100 < LEARN_CM / 2 && Quat.Apart(Gun.Grip.Q, now.Q) < LEARN_DEG / 2) return;
        Gun.HasGrip = true; Gun.Grip = now;
    }

    // ---- a spot is picked --------------------------------------------------------------------------------------------------------
    // null = done; else why not, in plain words
    public string Pick(Spot s) {
        if (!Freeze.GameConnected) return "No game is connected: switch Hand Freeze on before the game starts.";
        if (Gun == null || !Gun.HasGrip) return "Hand Freeze has not seen this gun in your hand yet: hold it still for a second, hands not frozen.";
        var r = Spots.Recipe(s, Gun);
        if (r == null) return "This spot cannot be put up from here yet.";
        if (!haveLevel) {
            if (!pose.Live(PoseShared.LIVE_HEAD)) return "The game is not telling where the headset is yet.";
            level = Pose.Level(pose.Head); haveLevel = true;
        }
        if (plain) { plain = false; }   // a carried plain freeze becomes the spot's pose, where it was carried to
        Picked = s; recipe = r; stepAt = 0; stepStart = DateTime.UtcNow;
        if (Freeze.Frozen != Hands.BOTH) Freeze.Freeze(Hands.BOTH, false);
        Fast(0);
        return null;
    }
    public void LetGoSpot() { Picked = null; recipe = null; if (!plain) pose.Clear(); }
    // back in front of the eyes, where the head looks now
    public void Recentre() { G = Pose.One; haveLevel = false; if (pose.Live(PoseShared.LIVE_HEAD)) { level = Pose.Level(pose.Head); haveLevel = true; } }
    void Reset() { Picked = null; recipe = null; plain = false; carryHand = 0; G = Pose.One; haveLevel = false; pose.Clear(); }

    // the two controllers for one step of a recipe (before the carry): the gun's zone `focus` at the anchor
    public static void Targets(Pose level, Quat view, V3 anchor, GunShape gun, V3 focus, Step st, out Pose left, out Pose right, out bool leftRides) {
        Quat qGun = (level.Q * view).Unit;
        var gunPose = new Pose(qGun, anchor - qGun.Turn(focus));
        right = gunPose * gun.Grip.Inverse;
        leftRides = true;
        var z = st.Zone == null ? null : gun.Zone(st.Zone);
        if (st.Pouch) { left = new Pose(level.Q, level.Apply(POUCH)); leftRides = false; }   // by the body: the carry does not take it along
        else if (st.Park || z == null) left = new Pose(level.Q, anchor + level.Q.Turn(PARK));
        else left = new Pose((qGun * ALONG_GUN).Unit, gunPose.Apply(z.P + st.Off));
    }
    public V3 Anchor { get { return level.Apply(new V3(0, HeightCm / 100.0, -DistanceCm / 100.0)); } }

    // ---- every frame while hands are frozen -----------------------------------------------------------------------------------
    public void Fast(double dt) {
        pose.Beat();
        if (Freeze.Frozen == 0) { if (Posing) Reset(); gripWasL = gripWasR = false; return; }
        Carry(dt);
        if (Picked != null && Gun != null && Gun.HasGrip && haveLevel) {
            while (stepAt < recipe.Count - 1 && (DateTime.UtcNow - stepStart).TotalMilliseconds >= recipe[stepAt].Ms) { stepAt++; stepStart = DateTime.UtcNow; }
            var st = recipe[stepAt];
            var zone = Picked.ZoneKey == null ? null : Gun.Zone(Picked.ZoneKey);
            Pose l, r; bool rides;
            Targets(level, View, Anchor, Gun, zone != null ? zone.P : Gun.Middle, st, out l, out r, out rides);
            pose.Give(Hands.BOTH, rides ? G * l : l, G * r, Hands.BOTH, st.LGrip, st.LTrig, 0, st.RTrig, 0, st.RHigh ? PoseShared.BTN_HIGH : 0);
        } else if (plain) {
            pose.Give(plainMask, G * plainL, G * plainR, 0, 0, 0, 0, 0, 0, 0);
        }
    }
    public bool RecipeDone { get { return recipe != null && stepAt == recipe.Count - 1; } }

    void Carry(double dt) {
        bool out_ = wv.PointersOut;
        bool gl = out_ && wv.Grip(Hands.LEFT) > GRAB_AT, gr = out_ && wv.Grip(Hands.RIGHT) > GRAB_AT;
        if (carryHand == 0) {
            int hand = gr && !gripWasR ? Hands.RIGHT : gl && !gripWasL ? Hands.LEFT : 0;
            if (hand != 0 && !wv.OnWindow(hand) && pose.Live(hand == Hands.LEFT ? PoseShared.LIVE_LEFT : PoseShared.LIVE_RIGHT)) {
                if (!Posing) {   // a plain freeze: take the hands where the game is told they are
                    plainMask = Freeze.Frozen;
                    bool okL = (plainMask & Hands.LEFT) == 0 || pose.Live(PoseShared.LIVE_LEFT), okR = (plainMask & Hands.RIGHT) == 0 || pose.Live(PoseShared.LIVE_RIGHT);
                    if (okL && okR) { plainL = pose.Told(Hands.LEFT); plainR = pose.Told(Hands.RIGHT); plain = true; G = Pose.One; }
                }
                if (Posing) { carryHand = hand; carryStart = pose.Real(hand); carryG0 = G; }
            }
        }
        gripWasL = gl; gripWasR = gr;
        if (carryHand == 0) return;
        bool live = pose.Live(carryHand == Hands.LEFT ? PoseShared.LIVE_LEFT : PoseShared.LIVE_RIGHT);
        if (!out_ || !live || wv.Grip(carryHand) < LET_GO_AT) { carryHand = 0; return; }
        Pose lv = haveLevel ? level : pose.Live(PoseShared.LIVE_HEAD) ? Pose.Level(pose.Head) : Pose.One;
        V3 middle = carryG0.Apply(Picked != null && haveLevel ? Anchor : MiddleOfPlain());
        carryG0 = StickMove(carryG0, lv, middle, wv.RightStickX, wv.RightStickY, wv.LeftStickY, dt);
        G = pose.Real(carryHand) * carryStart.Inverse * carryG0;
    }
    V3 MiddleOfPlain() {
        if (plainMask == Hands.LEFT) return plainL.P;
        if (plainMask == Hands.RIGHT) return plainR.P;
        return (plainL.P + plainR.P) * 0.5;
    }
    // the sticks on a carried rig: right stick left / right turns it about the upright through its middle, up / down tilts it
    // (top away / towards), the left stick up / down pushes it away / pulls it closer
    public static Pose StickMove(Pose g, Pose level, V3 middle, double rx, double ry, double ly, double dt) {
        if (Math.Abs(rx) < 0.15) rx = 0; if (Math.Abs(ry) < 0.15) ry = 0; if (Math.Abs(ly) < 0.15) ly = 0;
        if (rx == 0 && ry == 0 && ly == 0) return g;
        Quat turn = Quat.About(new V3(0, 1, 0), -rx * TURN_DEG_PER_S * dt) * Quat.About(level.Q.Turn(new V3(1, 0, 0)), -ry * TURN_DEG_PER_S * dt);
        Pose moved = Pose.AboutPoint(middle, turn.Unit) * g;
        moved.P = moved.P + level.Q.Turn(new V3(0, 0, -1)) * (ly * PUSH_M_PER_S * dt);
        return moved;
    }
}
