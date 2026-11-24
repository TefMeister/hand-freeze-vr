// HandCarry.cs -- what the window knows from the game (the gun in hand, its spots, where the hands are now), and the carry:
// with Window View's pointers out, a grip held off every window carries the frozen hands (v6, 2026-11-25).
//
// Tefa 2026-11-25: "to be able to move and rotate the frozen weapon with hands on it in the same way [as Window View's windows]
// without unfreezing them - while i got the pointers out". The carry is one rigid move G over the frozen hands, in the layer's
// room: a held grip (pointer off every window) drags G with the real controller, the right stick turns / tilts it, the left
// stick pushes it away / pulls it closer. Putting the hands on a clicked spot (v5) is gone with the 3D gun (archive/).
using System;
using System.Collections.Generic;

class HandCarry : IDisposable {
    const double GRAB_AT = 0.7, LET_GO_AT = 0.3;            // the grip's travel that takes hold / lets go
    const double TURN_DEG_PER_S = 60, PUSH_M_PER_S = 0.5;   // the sticks while carrying (Window View's own rates)

    public readonly Shared Freeze;
    readonly PoseShared pose = new PoseShared();
    readonly WindowViewLook wv = new WindowViewLook();

    public string ForcedGame;   // tests: a pretend game's folder
    int findTick;
    public string Game, Gun = "";
    public bool Answering;      // Hand Poser's game script wrote its state within the last seconds
    public List<Spot> SpotList = new List<Spot>();
    public string RealLeft = "", RealRight = "";
    public bool Carrying { get { return carryHand != 0; } }
    public bool PointersOut { get { return wv.PointersOut; } }

    Pose G = Pose.One;
    bool plain; int plainMask; Pose plainL, plainR;
    int carryHand; Pose carryStart, carryG0; bool gripWasL, gripWasR;

    public HandCarry(Shared freeze) { Freeze = freeze; }
    public void Dispose() { pose.Dispose(); wv.Dispose(); }

    // ---- a few times a second: the game, the gun, the spots ------------------------------------------------------------------
    public void Slow() {
        string found = ForcedGame ?? (Game == null || findTick++ % 8 == 0 ? GameFiles.Find(Freeze.GameConnected ? Freeze.LayerPid : 0) : null);
        if (found != null) Game = found;
        if (Game == null) { Answering = false; return; }
        string gun, spot;
        Answering = GameFiles.Now(Game, out gun, out spot);
        if (Answering) { Gun = gun; RealLeft = Spots.LeftPart(spot); RealRight = Spots.RightPart(spot); }
        else { RealLeft = ""; RealRight = ""; }   // the last gun's list stays up, nothing lit
        SpotList = Spots.List(Game, Gun);
    }

    void Reset() { plain = false; carryHand = 0; G = Pose.One; pose.Clear(); }

    // ---- every frame while hands are frozen -----------------------------------------------------------------------------------
    public void Fast(double dt) {
        pose.Beat();
        if (Freeze.Frozen == 0) { if (plain) Reset(); gripWasL = gripWasR = false; return; }
        Carry(dt);
        if (plain) pose.Give(plainMask, G * plainL, G * plainR, 0, 0, 0, 0, 0, 0, 0);
    }

    void Carry(double dt) {
        bool out_ = wv.PointersOut;
        bool gl = out_ && wv.Grip(Hands.LEFT) > GRAB_AT, gr = out_ && wv.Grip(Hands.RIGHT) > GRAB_AT;
        if (carryHand == 0) {
            int hand = gr && !gripWasR ? Hands.RIGHT : gl && !gripWasL ? Hands.LEFT : 0;
            if (hand != 0 && !wv.OnWindow(hand) && pose.Live(hand == Hands.LEFT ? PoseShared.LIVE_LEFT : PoseShared.LIVE_RIGHT)) {
                if (!plain) {   // the first carry of this freeze: take the hands where the game is told they are
                    plainMask = Freeze.Frozen;
                    bool okL = (plainMask & Hands.LEFT) == 0 || pose.Live(PoseShared.LIVE_LEFT), okR = (plainMask & Hands.RIGHT) == 0 || pose.Live(PoseShared.LIVE_RIGHT);
                    if (okL && okR) { plainL = pose.Told(Hands.LEFT); plainR = pose.Told(Hands.RIGHT); plain = true; G = Pose.One; }
                }
                if (plain) { carryHand = hand; carryStart = pose.Real(hand); carryG0 = G; }
            }
        }
        gripWasL = gl; gripWasR = gr;
        if (carryHand == 0) return;
        bool live = pose.Live(carryHand == Hands.LEFT ? PoseShared.LIVE_LEFT : PoseShared.LIVE_RIGHT);
        if (!out_ || !live || wv.Grip(carryHand) < LET_GO_AT) { carryHand = 0; return; }
        Pose lv = pose.Live(PoseShared.LIVE_HEAD) ? Pose.Level(pose.Head) : Pose.One;
        carryG0 = StickMove(carryG0, lv, carryG0.Apply(Middle()), wv.RightStickX, wv.RightStickY, wv.LeftStickY, dt);
        G = pose.Real(carryHand) * carryStart.Inverse * carryG0;
    }
    V3 Middle() {
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
