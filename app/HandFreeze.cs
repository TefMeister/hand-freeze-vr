// HandFreeze.cs -- the Hand Freeze desktop app: start-up, the shared switch, and the OpenXR registration. The window
// itself is FreezeUi.cs.
//
// Tefa, 2026-10-13: "a small program with an icon on the desktop, where i can just switch this on and off, a universal
// one for OpenXR, with a choice of seconds till the freeze and a button to unfreeze controllers". Then, the same day:
// summer background when off, winter when on, a round red/green switch, a snowflake FREEZE and a flame UNFREEZE button,
// rows for LEFT, RIGHT and BOTH, a pixel font, all pixelated.
//
// ON/OFF registers the OpenXR layer (hand_freeze_layer.dll, next to this exe) for every OpenXR game of this Windows user
// (HKCU, no admin), and since v3.1 a 32-bit copy beside it, so 32-bit games start too; since v3.2 the registration is
// checked against the files and mended each time the app opens (Layer.Mend). A game picks the layer up when it starts,
// so the switch has to be on before the game starts; the window can be opened and closed at any time. FREEZE counts down, then tells the layer which hands to hold; UNFREEZE lets them go.
// Closing the window always unfreezes. The switch is the shared-memory block of ../shared.h (offsets below match it).
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program {
    [STAThread]
    static void Main() {
        bool first;
        using (var one = new Mutex(true, "FlatToVR_HandFreeze_App", out first)) {
            if (!first) { MessageBox.Show("Hand Freeze is already open.", "Hand Freeze"); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FreezeUi());
        }
    }
}

// which hands: the bits of the shared block's `frozen`
static class Hands { public const int LEFT = 1, RIGHT = 2, BOTH = 3; }

class Shared : IDisposable {
    const string NAME = @"Local\FlatToVR_HandFreeze_v3";
    const uint MAGIC = 0x5A465248, VERSION = 3;
    const int SIZE = 188, FROZEN = 8, GEN_LEFT = 12, HEARTBEAT = 16, CONTROLLERS = 28, GAME_NAME = 40,
              GEN_RIGHT = 168, LEFT_SPACES = 172, RIGHT_SPACES = 176, BUTTONS = 180, INPUTS_HELD = 184;
    const ulong CONNECTED_WITHIN_MS = 3000;   // a game counts as connected if the layer was heard from this recently
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;

    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    public Shared() {
        file = MemoryMappedFile.CreateOrOpen(NAME, SIZE);
        view = file.CreateViewAccessor(0, SIZE);
        if (view.ReadUInt32(0) != MAGIC) { view.Write(4, VERSION); view.Write(0, MAGIC); }
    }
    public int Frozen { get { return view.ReadInt32(FROZEN); } }
    public int ButtonsFrozen { get { return view.ReadInt32(BUTTONS); } }   // v3: hands whose buttons are held too
    public int InputsHeld { get { return view.ReadInt32(INPUTS_HELD); } }
    public void Freeze(int hands, bool buttonsToo) {
        if ((hands & Hands.LEFT) != 0) view.Write(GEN_LEFT, view.ReadInt32(GEN_LEFT) + 1);   // fresh poses for these hands
        if ((hands & Hands.RIGHT) != 0) view.Write(GEN_RIGHT, view.ReadInt32(GEN_RIGHT) + 1);
        view.Write(BUTTONS, buttonsToo ? ButtonsFrozen | hands : ButtonsFrozen & ~hands);   // before the poses: one freeze, both taken at the same generation
        view.Write(FROZEN, Frozen | hands);
    }
    public void Unfreeze(int hands) { view.Write(FROZEN, Frozen & ~hands); view.Write(BUTTONS, ButtonsFrozen & ~hands); }
    public int Controllers { get { return view.ReadInt32(CONTROLLERS); } }
    public int LeftSpaces { get { return view.ReadInt32(LEFT_SPACES); } }
    public int RightSpaces { get { return view.ReadInt32(RIGHT_SPACES); } }
    public bool GameConnected { get { ulong hb = view.ReadUInt64(HEARTBEAT); return hb != 0 && GetTickCount64() - hb < CONNECTED_WITHIN_MS; } }
    public string GameName {
        get {
            var chars = new char[64];
            view.ReadArray(GAME_NAME, chars, 0, 64);
            int n = Array.IndexOf(chars, '\0');
            return new string(chars, 0, n < 0 ? 64 : n);
        }
    }
    public void Dispose() { view.Dispose(); file.Dispose(); }
}

static class Layer {
    const string KEY = @"Software\Khronos\OpenXR\1\ApiLayers\Implicit";
    public const string LAYER_NAME = "XR_APILAYER_FLATTOVR_hand_freeze";
    static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    public static string Dll { get { return Path.Combine(Dir, "hand_freeze_layer.dll"); } }
    public static string Manifest { get { return Path.Combine(Dir, "hand_freeze_layer.json"); } }
    // v3.1 (2026-10-14): a 32-bit copy of the layer, registered beside the 64-bit one. The per-user layer list is read by
    // 32-bit and 64-bit games alike, and a game whose loader can load NO layer from the list fails to start (a 32-bit
    // Manhunt did, 2026-10-14). The loader forgives a layer it cannot load as long as another one loads, so each game
    // loads the copy of its own size and skips the other with a warning.
    public static string Dll32 { get { return Path.Combine(Dir, "hand_freeze_layer32.dll"); } }
    public static string Manifest32 { get { return Path.Combine(Dir, "hand_freeze_layer32.json"); } }

    public static bool IsOn() {
        using (var k = Registry.CurrentUser.OpenSubKey(KEY)) {
            object v = k == null ? null : k.GetValue(Manifest);
            return v is int && (int)v == 0;
        }
    }
    static string ManifestText(string name, string dll, string bits) {
        // the manifest names the DLL by its full path, so it works wherever the folder was put
        return "{\n  \"file_format_version\": \"1.0.0\",\n  \"api_layer\": {\n    \"name\": \"" + name + "\",\n" +
            "    \"library_path\": \"" + dll.Replace("\\", "\\\\") + "\",\n    \"api_version\": \"1.0\",\n    \"implementation_version\": \"3\",\n" +
            "    \"description\": \"Hand Freeze (" + bits + "): holds the controllers still on request, from the Hand Freeze app\",\n" +
            "    \"disable_environment\": \"DISABLE_HAND_FREEZE_LAYER\"\n  }\n}\n";
    }
    public static void TurnOn() {
        File.WriteAllText(Manifest, ManifestText(LAYER_NAME, Dll, "64-bit"));
        using (var k = Registry.CurrentUser.CreateSubKey(KEY)) {
            k.SetValue(Manifest, 0, RegistryValueKind.DWord);
            if (File.Exists(Dll32)) {
                File.WriteAllText(Manifest32, ManifestText(LAYER_NAME + "32", Dll32, "32-bit"));
                k.SetValue(Manifest32, 0, RegistryValueKind.DWord);
            } else k.DeleteValue(Manifest32, false);
        }
    }
    public static void TurnOff() {
        using (var k = Registry.CurrentUser.OpenSubKey(KEY, true)) if (k != null) { k.DeleteValue(Manifest, false); k.DeleteValue(Manifest32, false); }
    }
    // v3.2 (2026-10-16): the registration is checked against the files each time the app opens, and mended. A PC where
    // ON had been pressed before v3.1 was installed kept only the 64-bit copy registered, so its 32-bit OpenXR games
    // still failed to start (Hard Reset's own no-game test showed it, 2026-10-16); pressing OFF and ON again was the
    // cure, and nothing said so. Mend() does that press when the registered set does not match the files present.
    public static bool Mend() {
        if (!IsOn()) return false;
        bool fine;
        using (var k = Registry.CurrentUser.OpenSubKey(KEY)) {
            bool want32 = File.Exists(Dll32), has32 = k != null && k.GetValue(Manifest32) is int && File.Exists(Manifest32);
            fine = want32 == has32 && File.Exists(Manifest);
        }
        if (fine) return false;
        TurnOn();
        return true;
    }
}
