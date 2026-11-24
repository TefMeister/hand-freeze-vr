// PoseLink.cs -- the second shared block (shared.h v5: the given poses) and a read-only look at Window View's blocks.
using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

class PoseShared : IDisposable {
    const string NAME = @"Local\FlatToVR_HandFreeze_Pose_v1";
    const uint MAGIC = 0x5A504648, VERSION = 1;
    const int SIZE = 264, POSED = 8, SYNTH = 12, POSE_LEFT = 16, POSE_RIGHT = 44, SQUEEZE = 72, TRIGGER = 80, BUTTONS = 88, LIVE_VALID = 96,
              HEAD = 100, REAL_LEFT = 128, REAL_RIGHT = 156, EFF_LEFT = 184, EFF_RIGHT = 212, LIVE_MS = 240, APP_HEARTBEAT = 248, INPUTS_POSED = 256;
    public const int LIVE_HEAD = 1, LIVE_LEFT = 2, LIVE_RIGHT = 4, BTN_LOW = 1, BTN_HIGH = 2;
    const ulong LIVE_WITHIN_MS = 500;
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    public PoseShared() {
        file = MemoryMappedFile.CreateOrOpen(NAME, SIZE);
        view = file.CreateViewAccessor(0, SIZE);
        if (view.ReadUInt32(0) != MAGIC) { view.Write(4, VERSION); view.Write(0, MAGIC); }
    }
    Pose Read(int at) {
        return new Pose(new Quat(view.ReadSingle(at + 12), view.ReadSingle(at + 16), view.ReadSingle(at + 20), view.ReadSingle(at + 24)).Unit,
                        new V3(view.ReadSingle(at), view.ReadSingle(at + 4), view.ReadSingle(at + 8)));
    }
    void Write(int at, Pose p) {
        view.Write(at, (float)p.P.X); view.Write(at + 4, (float)p.P.Y); view.Write(at + 8, (float)p.P.Z);
        view.Write(at + 12, (float)p.Q.X); view.Write(at + 16, (float)p.Q.Y); view.Write(at + 20, (float)p.Q.Z); view.Write(at + 24, (float)p.Q.W);
    }
    public bool Live(int bit) { ulong t = view.ReadUInt64(LIVE_MS); return t != 0 && GetTickCount64() - t < LIVE_WITHIN_MS && (view.ReadInt32(LIVE_VALID) & bit) != 0; }
    public Pose Head { get { return Read(HEAD); } }
    public Pose Real(int hand) { return Read(hand == Hands.LEFT ? REAL_LEFT : REAL_RIGHT); }
    public Pose Told(int hand) { return Read(hand == Hands.LEFT ? EFF_LEFT : EFF_RIGHT); }   // what the game is told
    public int Posed { get { return view.ReadInt32(POSED); } }
    public int InputsPosed { get { return view.ReadInt32(INPUTS_POSED); } }
    public void Beat() { view.Write(APP_HEARTBEAT, GetTickCount64()); }

    // the poses first, the masks last: the layer never reads a mask whose pose is not there yet
    public void Give(int posed, Pose left, Pose right, int synth, double lGrip, double lTrig, double rGrip, double rTrig, int lButtons, int rButtons) {
        Write(POSE_LEFT, left); Write(POSE_RIGHT, right);
        view.Write(SQUEEZE, (float)lGrip); view.Write(SQUEEZE + 4, (float)rGrip);
        view.Write(TRIGGER, (float)lTrig); view.Write(TRIGGER + 4, (float)rTrig);
        view.Write(BUTTONS, lButtons); view.Write(BUTTONS + 4, rButtons);
        view.Write(SYNTH, synth); view.Write(POSED, posed);
        Beat();
    }
    public void Clear() { view.Write(POSED, 0); view.Write(SYNTH, 0); }
    public void Dispose() { Clear(); view.Write(APP_HEARTBEAT, 0UL); view.Dispose(); file.Dispose(); }
}

// Window View's controllers, read only (window-view-vr shared.h): its grips and sticks, and whether each hand's pointer is on a
// window. Nothing here is created: with Window View closed every answer is "no".
class WindowViewLook : IDisposable {
    const int MAIN_SIZE = 644, APP_HEARTBEAT = 8, CURSOR_VISIBLE = 512, CURSOR_EDGE = 532, CTRL_MODE = 536, SQUEEZE = 576, STICK_Y = 580,
              LSQUEEZE = 620, LSTICK_Y = 624, STICK_X = 636, LSTICK_X = 640;
    const int KEYS_SIZE = 192, LEFT_CURSOR_VISIBLE = 28 + 60, RHIT = 188;
    MemoryMappedViewAccessor main, keys;
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    static MemoryMappedViewAccessor Open(string name, int size) {
        try { using (var f = MemoryMappedFile.OpenExisting(name)) return f.CreateViewAccessor(0, size); } catch (Exception) { return null; }
    }
    bool Ready() {
        if (main == null) main = Open(@"Local\FlatToVR_WindowView_v2", MAIN_SIZE);
        if (keys == null) keys = Open(@"Local\FlatToVR_WindowView_keys_v1", KEYS_SIZE);
        if (main == null) return false;
        ulong t = main.ReadUInt64(APP_HEARTBEAT);
        return t != 0 && GetTickCount64() - t < 3000;
    }
    // the controllers work the windows (the pointers are out)
    public bool PointersOut { get { return Ready() && main.ReadInt32(CTRL_MODE) != 0; } }
    public double Grip(int hand) { return !Ready() ? 0 : main.ReadSingle(hand == Hands.LEFT ? LSQUEEZE : SQUEEZE); }
    public bool OnWindow(int hand) {
        if (!Ready()) return false;
        if (hand == Hands.LEFT) return keys != null && keys.ReadInt32(LEFT_CURSOR_VISIBLE) != 0;
        return (main.ReadInt32(CURSOR_VISIBLE) != 0 && main.ReadInt32(CURSOR_EDGE) == 0) || (keys != null && keys.ReadInt32(RHIT) != 0);
    }
    public double RightStickX { get { return Ready() ? main.ReadSingle(STICK_X) : 0; } }
    public double RightStickY { get { return Ready() ? main.ReadSingle(STICK_Y) : 0; } }
    public double LeftStickY { get { return Ready() ? main.ReadSingle(LSTICK_Y) : 0; } }
    public void Dispose() { if (main != null) main.Dispose(); if (keys != null) keys.Dispose(); }
}
