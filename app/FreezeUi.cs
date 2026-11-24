// FreezeUi.cs -- the Hand Freeze window, in the look of the helper family (2026-11-25, Tefa: "scrap the hand freeze design and
// make it look like the other apps in the family"; until then a pixel-art window with a snowflake and a flame, kept in git).
//
// Two tabs. FREEZE: the ON / OFF switch, the seconds, and LEFT HAND / RIGHT HAND / BOTH HANDS with FREEZE and UN-FREEZE.
// SPOTS: a rough picture of the gun in hand that turns all the way round, with every numbered hand spot hung on it (GunView.cs);
// a click on a spot freezes both hands and raises them in front of the eyes, the gun turned as the picture is, the left hand on
// that spot (SpotEngine.cs). With Window View's pointers out, a grip held off every window carries the frozen hands.
// STILL: nothing here animates; a label or the picture is redrawn only when what it shows changed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

class FreezeUi : Form {
    const int STEP_MS = 100, FAST_MS = 15, SLOW_EVERY = 3;   // the shared block is looked at; the given poses are written; the game's files are read every 3rd step
    const int DEFAULT_SECONDS = 10, MAX_SECONDS = 60;
    const int TICK_HZ = 880, TICK_MS = 70, FROZEN_HZ = 1320, FROZEN_MS = 350;   // the countdown beeps, the final one
    const int TAB_H = 44, SIDE_W = 268, TURN_STEP_DEG = 15;
    static readonly Size FREEZE_SIZE = new Size(640, 320), SPOTS_SIZE = new Size(1240, 720);
    static readonly Color Ink = Look.Ink, Dim = Look.Dim;

    readonly Shared shared = new Shared();
    readonly SpotEngine engine;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer(), fast = new System.Windows.Forms.Timer();
    readonly string settingsFile;
    readonly Options opt;

    bool on, pinned, buttonsToo, stickToggle = true;
    int seconds = DEFAULT_SECONDS, toggleSeq = -1, countdownHands, lastBeep = -1, steps;
    DateTime countdownEnd, lastFast = DateTime.UtcNow;
    string tab = "freeze", spotNote = "";

    SkinButton tabFreeze, tabSpots, switchButton;
    SkinLabel status, secondsLabel, headline, spotStatus;
    SkinPanel freezePanel, spotsPanel;
    SkinCheckBox buttonsBox, stickBox, pinBox;
    readonly SkinLabel[] rowState = new SkinLabel[3];
    GunView gunView; FancySlider distance, height; SkinLabel distanceLabel, heightLabel;

    static readonly string[] RowName = { "LEFT HAND", "RIGHT HAND", "BOTH HANDS" };
    static readonly int[] RowHands = { Hands.LEFT, Hands.RIGHT, Hands.BOTH };

    public class Options { public string Game, Tab, Shot, Pick; public bool Test; }

    public FreezeUi(Options options) {
        opt = options;
        settingsFile = Path.Combine(opt.Test && opt.Game != null ? opt.Game : AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
        engine = new SpotEngine(shared) { ForcedGame = opt.Game };
        Text = "Hand Freeze";
        string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hand_freeze.ico");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; BackColor = Look.Back; ForeColor = Ink;
        Font = new Font("Segoe UI", 9.5f);
        Backdrop.Init(this, ico, Look.Back);
        if (!opt.Test) Layer.Mend();   // a stale registration is re-done here (v3.2)
        on = Layer.IsOn();
        LoadSettings();
        if (opt.Tab != null) tab = opt.Tab;
        TopMost = pinned;
        Build();
        ShowTab(tab);

        tick.Interval = STEP_MS; tick.Tick += (s, e) => Step(); tick.Start();
        fast.Interval = FAST_MS; fast.Tick += (s, e) => Fast();
        FormClosing += (s, e) => { shared.Unfreeze(Hands.BOTH); shared.Tell(false, false); engine.Dispose(); shared.Dispose(); };
        if (opt.Shot != null) {   // tests: a picture of the window, then close
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); using (var b = new Bitmap(ClientSize.Width, ClientSize.Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, ClientSize)); b.Save(opt.Shot, ImageFormat.Png); } Close(); };
            t.Start();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { Backdrop.PaintUnder(this, e.Graphics, e.ClipRectangle); }

    // ---- building ---------------------------------------------------------------------------------------------------------------
    SkinButton Btn(string text, int w, int h, Color back, EventHandler click) {
        var b = new SkinButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Ink, Width = w, Height = h, Margin = new Padding(3), Font = new Font("Segoe UI Semibold", 9.5f) };
        b.FlatAppearance.BorderSize = 0; b.Click += click;
        return b;
    }
    SkinLabel Lbl(string text, float pt, Color ink, int w, int h) {
        return new SkinLabel { Text = text, Font = new Font("Segoe UI Semibold", pt), ForeColor = ink, Width = w, Height = h, TextAlign = ContentAlignment.MiddleLeft };
    }
    SkinCheckBox Tick(string text, bool value, Action<bool> changed) {
        var c = new SkinCheckBox { Text = text, Checked = value, ForeColor = Ink, AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
        c.CheckedChanged += (s, e) => { changed(c.Checked); SaveSettings(); };
        return c;
    }
    static T Put<T>(Control parent, T c, int x, int y) where T : Control { c.Location = new Point(x, y); parent.Controls.Add(c); return c; }

    void Build() {
        var top = new SkinPanel { Dock = DockStyle.Top, Height = TAB_H, Tint = Color.FromArgb(150, Look.Back) };
        tabFreeze = Put(top, Btn("FREEZE", 110, 32, Look.ButtonBack, (s, e) => ShowTab("freeze")), 8, 6);
        tabSpots = Put(top, Btn("SPOTS", 110, 32, Look.ButtonBack, (s, e) => ShowTab("spots")), 122, 6);
        switchButton = Put(top, Btn("OFF", 70, 32, Look.Bad, (s, e) => Toggle()), 244, 6);
        status = Put(top, Lbl("", 9.5f, Dim, 320, 32), 322, 6);

        freezePanel = new SkinPanel { Dock = DockStyle.Fill };
        Put(freezePanel, Lbl("SECONDS TO FREEZE", 9.5f, Dim, 150, 30), 12, 10);
        Put(freezePanel, Btn("−", 34, 30, Look.ButtonBack, (s, e) => SetSeconds(seconds - 1)), 166, 10);
        secondsLabel = Put(freezePanel, Lbl("", 14f, Ink, 44, 30), 206, 10); secondsLabel.TextAlign = ContentAlignment.MiddleCenter;
        Put(freezePanel, Btn("+", 34, 30, Look.ButtonBack, (s, e) => SetSeconds(seconds + 1)), 254, 10);
        var ticks = Put(freezePanel, new FlowLayoutPanel { Width = 620, Height = 34, BackColor = Color.Transparent, WrapContents = false }, 8, 46);
        ticks.Controls.Add(buttonsBox = Tick("BUTTONS TOO", buttonsToo, v => buttonsToo = v));
        ticks.Controls.Add(stickBox = Tick("RIGHT STICK CLICK FREEZES", stickToggle, v => stickToggle = v));
        ticks.Controls.Add(pinBox = Tick("ALWAYS ON TOP", pinned, v => { pinned = v; TopMost = v; }));
        for (int i = 0; i < 3; i++) {
            int hands = RowHands[i], y = 86 + i * 60;
            var box = Put(freezePanel, new SkinPanel { Width = 616, Height = 52, Tint = Look.BoxTint }, 12, y);
            Put(box, Lbl(RowName[i], 14f, Ink, 170, 40), 12, 6);
            Put(box, Btn("FREEZE", 110, 38, Look.Accent, (s, e) => StartFreeze(hands)), 190, 7);
            Put(box, Btn("UN-FREEZE", 110, 38, Color.FromArgb(150, 84, 60), (s, e) => { if ((countdownHands & hands) != 0) countdownHands = 0; shared.Unfreeze(hands); Step(); }), 308, 7);
            rowState[i] = Put(box, Lbl("", 11f, Look.Good, 180, 40), 430, 6);
        }
        MouseWheel += (s, e) => { if (tab == "freeze") SetSeconds(seconds + Math.Sign(e.Delta)); };

        spotsPanel = new SkinPanel { Dock = DockStyle.Fill };
        var side = new SkinPanel { Dock = DockStyle.Right, Width = SIDE_W, Tint = Color.FromArgb(120, Look.Back) };
        headline = Put(side, Lbl("", 11f, Ink, SIDE_W - 16, 48), 8, 6);
        Put(side, Lbl("LOOK AT THE GUN FROM", 9f, Dim, SIDE_W - 16, 22), 8, 58);
        string[] sides = { "LEFT SIDE", "RIGHT SIDE", "ABOVE", "BELOW", "THE MUZZLE", "BEHIND" };
        for (int i = 0; i < sides.Length; i++) { int k = i; Put(side, Btn(sides[i], 122, 30, Look.ButtonBack, (s, e) => gunView.SetView(GunView.Sides[k])), 8 + (i % 2) * 126, 82 + (i / 2) * 34); }
        Put(side, Lbl("TURN IT", 9f, Dim, SIDE_W - 16, 22), 8, 188);
        string[] turns = { "◀", "▶", "▲", "▼", "↺", "↻" };
        double[][] by = { new[] { -1.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, -1.0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, 0, -1.0 } };
        for (int i = 0; i < turns.Length; i++) { var d = by[i]; Put(side, Btn(turns[i], 38, 32, Look.ButtonBack, (s, e) => gunView.TurnBy(d[0] * TURN_STEP_DEG, d[1] * TURN_STEP_DEG, d[2] * TURN_STEP_DEG)), 8 + i * 42, 212); }
        distanceLabel = Put(side, Lbl("", 9f, Dim, SIDE_W - 16, 22), 8, 256);
        distance = Put(side, new FancySlider { Minimum = 25, Maximum = 80, Value = engine.DistanceCm, Width = SIDE_W - 20 }, 8, 278);
        heightLabel = Put(side, Lbl("", 9f, Dim, SIDE_W - 16, 22), 8, 316);
        height = Put(side, new FancySlider { Minimum = -30, Maximum = 20, Signed = true, Value = engine.HeightCm, Width = SIDE_W - 20 }, 8, 338);
        distance.ValueChanged += (s, e) => { engine.DistanceCm = distance.Value; SideWords(); SaveSettings(); };
        height.ValueChanged += (s, e) => { engine.HeightCm = height.Value; SideWords(); SaveSettings(); };
        Put(side, Btn("BACK IN FRONT OF ME", SIDE_W - 20, 32, Look.ButtonBack, (s, e) => engine.Recentre()), 8, 382);
        Put(side, Btn("LET THE SPOT GO (STAY FROZEN)", SIDE_W - 20, 32, Look.ButtonBack, (s, e) => { engine.LetGoSpot(); RefreshSpots(); }), 8, 418);
        Put(side, Btn("UN-FREEZE BOTH HANDS", SIDE_W - 20, 32, Color.FromArgb(150, 84, 60), (s, e) => { countdownHands = 0; shared.Unfreeze(Hands.BOTH); Step(); }), 8, 454);
        Put(side, Btn("PUT THE SCRIPT INTO THE GAME", SIDE_W - 20, 32, Look.ButtonBack, (s, e) => InstallScript()), 8, 500);
        spotStatus = Put(side, new SkinLabel { ForeColor = Dim, Width = SIDE_W - 16, Height = 150, Font = new Font("Segoe UI", 9f) }, 8, 540);
        gunView = new GunView { Dock = DockStyle.Fill };
        gunView.Turned += () => engine.View = gunView.View;
        gunView.SpotClicked += sp => {
            if (engine.PickedCode == sp.Hand + sp.Code) { engine.LetGoSpot(); spotNote = ""; }
            else spotNote = engine.Pick(sp) ?? "";
            Step();
        };
        spotsPanel.Controls.Add(gunView); spotsPanel.Controls.Add(side);

        Controls.Add(freezePanel); Controls.Add(spotsPanel); Controls.Add(top);
        SideWords();
    }
    void SideWords() {
        Set(distanceLabel, "HOW FAR IN FRONT OF YOU: " + engine.DistanceCm + " cm");
        Set(heightLabel, "ABOVE / BELOW YOUR EYES: " + (engine.HeightCm > 0 ? "+" : "") + engine.HeightCm + " cm");
    }
    static void Set(Control c, string text) { if (c.Text != text) c.Text = text; }
    static void Tint(Control c, Color back) { if (c.BackColor != back) c.BackColor = back; }

    void ShowTab(string name) {
        tab = name == "spots" ? "spots" : "freeze";
        freezePanel.Visible = tab == "freeze"; spotsPanel.Visible = tab == "spots";
        Tint(tabFreeze, tab == "freeze" ? Look.Accent : Look.ButtonBack); Tint(tabSpots, tab == "spots" ? Look.Accent : Look.ButtonBack);
        var size = tab == "spots" ? SPOTS_SIZE : FREEZE_SIZE;
        if (ClientSize != size) ClientSize = size;
        SaveSettings();
        Step();
    }

    // ---- settings -----------------------------------------------------------------------------------------------------------------
    void LoadSettings() {
        try {
            var l = File.ReadAllLines(settingsFile);
            int v;
            if (l.Length > 0 && int.TryParse(l[0].Trim(), out v) && v >= 0 && v <= MAX_SECONDS) seconds = v;
            pinned = l.Length > 1 && l[1].Trim() == "pinned";
            buttonsToo = l.Length > 2 && l[2].Trim() == "buttons";
            stickToggle = l.Length <= 3 || l[3].Trim() != "no stick";   // on unless switched off
            if (l.Length > 4 && l[4].Trim() == "spots") tab = "spots";
            if (l.Length > 5 && int.TryParse(l[5].Trim(), out v) && v >= 25 && v <= 80) engine.DistanceCm = v;
            if (l.Length > 6 && int.TryParse(l[6].Trim(), out v) && v >= -30 && v <= 20) engine.HeightCm = v;
        } catch (Exception) { }
    }
    void SaveSettings() {
        if (freezePanel == null) return;   // still being built
        try { File.WriteAllLines(settingsFile, new[] { seconds.ToString(), pinned ? "pinned" : "free", buttonsToo ? "buttons" : "hands only", stickToggle ? "stick" : "no stick", tab, engine.DistanceCm.ToString(), engine.HeightCm.ToString() }); } catch (Exception) { }
    }

    // ---- behaviour ----------------------------------------------------------------------------------------------------------------
    void StartFreeze(int hands) {
        if (!on) return;
        countdownHands = hands; countdownEnd = DateTime.Now.AddSeconds(seconds); lastBeep = -1;
        Step();
    }
    void SetSeconds(int v) { seconds = Math.Max(0, Math.Min(MAX_SECONDS, v)); SaveSettings(); Step(); }
    static void Beep(int hz, int ms) { ThreadPool.QueueUserWorkItem(_ => { try { Console.Beep(hz, ms); } catch (Exception) { } }); }

    void Toggle() {
        try {
            if (!on) {
                if (!File.Exists(Layer.Dll)) { MessageBox.Show("hand_freeze_layer.dll is missing beside the app.", "Hand Freeze"); return; }
                Layer.TurnOn(); on = true;
            } else { countdownHands = 0; shared.Unfreeze(Hands.BOTH); Layer.TurnOff(); on = false; }
        } catch (Exception ex) { MessageBox.Show("Could not switch: " + ex.Message, "Hand Freeze"); }
        Step();
    }

    void Step() {
        var now = DateTime.Now;
        shared.Tell(on && stickToggle, buttonsToo);   // v4: the layer acts on the click only while this is fresh
        int seq = shared.ToggleSeq;
        if (toggleSeq >= 0 && seq != toggleSeq) { countdownHands = 0; Beep(shared.Frozen != 0 ? FROZEN_HZ : TICK_HZ, shared.Frozen != 0 ? FROZEN_MS : TICK_MS * 2); }
        toggleSeq = seq;
        if (countdownHands != 0) {
            double left = (countdownEnd - now).TotalSeconds;
            if (left <= 0) { shared.Freeze(countdownHands, buttonsToo); countdownHands = 0; Beep(FROZEN_HZ, FROZEN_MS); }
            else { int whole = (int)Math.Ceiling(left); if (whole != lastBeep) { lastBeep = whole; Beep(TICK_HZ, TICK_MS); } }
        }
        bool frozen = shared.Frozen != 0;
        if (fast.Enabled != frozen) { lastFast = DateTime.UtcNow; fast.Enabled = frozen; if (!frozen) engine.Fast(0); }
        if (steps++ % SLOW_EVERY == 0) { engine.Slow(); RefreshSpots(); }

        Set(switchButton, on ? "ON" : "OFF"); Tint(switchButton, on ? Color.FromArgb(52, 140, 78) : Color.FromArgb(170, 70, 60));
        string hands = (shared.LeftSpaces > 0 ? "L" : "") + (shared.RightSpaces > 0 ? "R" : "");
        Set(status, !on ? "Switched off. Switch on before the game starts." : !shared.GameConnected ? "No game connected yet." : "Connected: " + shared.GameName + (hands.Length > 0 ? "  (" + hands + ")" : ""));
        Set(secondsLabel, seconds.ToString());
        for (int i = 0; i < 3; i++) {
            int h = RowHands[i];
            bool isFrozen = (shared.Frozen & h) == h, buttons = isFrozen && (shared.ButtonsFrozen & h) == h, counting = countdownHands == h;
            Set(rowState[i], buttons ? "FROZEN + BUTTONS" : isFrozen ? "FROZEN" : counting ? "HOLD STILL  " + Math.Max(1, (int)Math.Ceiling((countdownEnd - now).TotalSeconds)) : "");
            var ink = isFrozen ? Look.Good : Look.Warn; if (rowState[i].ForeColor != ink) rowState[i].ForeColor = ink;
        }
    }
    void Fast() {
        var now = DateTime.UtcNow; double dt = Math.Min(0.1, (now - lastFast).TotalSeconds); lastFast = now;
        engine.Fast(dt);
    }

    void RefreshSpots() {
        if (tab != "spots") return;
        var g = engine.Gun;
        string picked = engine.Picked == null ? "" : engine.Picked.Code + " " + engine.Picked.Words;
        Set(headline, g == null ? "NO GUN YET" : g.Gun.Replace("_Inventory", "") + (picked != "" ? "\n" + (engine.Picked.Hand == "r" ? "RIGHT" : "LEFT") + " HAND: " + picked : "\nno spot picked"));
        string note = spotNote;
        if (note == "" && engine.Game == null) note = "No RE game found yet: start the game (Hand Freeze ON first).";
        else if (note == "" && g != null && !g.HasGrip) note = "Learning how this gun sits in your hand: hold it still for a second, hands not frozen.";
        gunView.Show(g, engine.SpotList, engine.PickedCode, engine.RealLeft, engine.RealRight, note);
        string s = "";
        if (engine.Picked != null) {
            bool there = engine.Picked.Part == (engine.Picked.Hand == "r" ? engine.RealRight : engine.RealLeft);
            s = !engine.RecipeDone ? "Putting the hands up..." : there ? "The game says the hand is on this spot." : "The hands are up. The game says the left hand is at: " + (engine.RealLeft == "" ? "no spot" : engine.RealLeft) + ".";
        } else if (shared.Frozen != 0) s = "Hands frozen. Click a spot to put them on it.";
        else s = "Click a spot: both hands freeze and come up in front of you on it, the gun turned like the picture.";
        s += "\n\nPointers out: hold a grip away from every window to carry the frozen hands; right stick turns and tilts, left stick pushes and pulls." + (engine.Carrying ? "\n\nCARRYING." : "");
        s += "\n\nGreen edge = where the game says the hand is now.";
        Set(spotStatus, s);
    }

    void InstallScript() {
        string game = engine.Game ?? GunFiles.Installed();
        string src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hand_freeze_spots.lua");
        if (game == null) { spotNote = "No RE game found: start the game once, then press this again."; RefreshSpots(); return; }
        try {
            Directory.CreateDirectory(Path.Combine(game, "reframework", "autorun"));
            Directory.CreateDirectory(GunFiles.Data(game));
            File.Copy(src, Path.Combine(game, "reframework", "autorun", "hand_freeze_spots.lua"), true);
            spotNote = "The script is in " + Path.GetFileName(game) + ". It starts with the game (or REFramework's Reset Scripts).";
        } catch (Exception ex) { spotNote = "Could not put the script in: " + ex.Message; }
        RefreshSpots();
    }
}
