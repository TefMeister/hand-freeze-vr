// FreezeUi.cs -- the Hand Freeze window, in the look of the helper family.
//
// v6 (2026-11-25, Tefa: "scrap the 3d gun from it and just leave the spots written down, make it all the same window, so on one
// side you have the hand freeze buttons and next to it are all the spots on the weapon ... just have these informative, so they
// light up ... and they change with the weapon at hand"). ONE page, wider not taller:
//   left    the ON / OFF switch, the seconds, and LEFT HAND / RIGHT HAND / BOTH HANDS with FREEZE and UN-FREEZE
//   right   every hand spot of the gun in hand, as Hand Poser numbers them (SpotList.cs): a row is GREEN while the game says the
//           hand is on that spot, and has a dot when Hand Poser holds a pose of its own for it. Nothing there is clicked.
// With Window View's pointers out, a grip held off every window still carries the frozen hands (HandCarry.cs).
// STILL: nothing here animates; a label is redrawn only when what it shows changed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// one spot's row: its number, a dot when Hand Poser has a pose saved for it, its words, each at its own fixed place so the
// columns line up; the back colour (set by the window) is the light
class SpotRow : SkinLabel {
    public string Code = "", Words = ""; public bool Dot;
    const int CODE_X = 6, DOT_X = 40, WORDS_X = 58;
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);   // the back colour only: Text stays empty
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(e.Graphics, Code, Font, new Rectangle(CODE_X, 0, DOT_X - CODE_X, Height), ForeColor, flags);
        if (Dot) TextRenderer.DrawText(e.Graphics, "●", Font, new Rectangle(DOT_X, 0, WORDS_X - DOT_X, Height), ForeColor, flags);
        TextRenderer.DrawText(e.Graphics, Words, Font, new Rectangle(WORDS_X, 0, Width - WORDS_X - 2, Height), ForeColor, flags);
    }
}

class FreezeUi : Form {
    const int STEP_MS = 100, FAST_MS = 15, SLOW_EVERY = 3;   // the shared block is looked at; the carried poses are written; the game's files are read every 3rd step
    const int DEFAULT_SECONDS = 10, MAX_SECONDS = 60;
    const int TICK_HZ = 880, TICK_MS = 70, FROZEN_HZ = 1320, FROZEN_MS = 350;   // the countdown beeps, the final one
    const int TOP_H = 44, LEFT_W = 640, SPOTS_W = 920, PAGE_H = 376;
    const int ROW_H = 24, ROWS_PER_COLUMN = 13, SPOTS_TOP = 62;   // 13 rows a column: a pistol's 24 left-hand spots take two columns
    static readonly Color Ink = Look.Ink, Dim = Look.Dim;
    static readonly Color HereBack = Color.FromArgb(52, 140, 78), SavedBack = Color.FromArgb(150, 38, 41, 52);

    readonly Shared shared = new Shared();
    readonly HandCarry carry;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer(), fast = new System.Windows.Forms.Timer();
    readonly string settingsFile;
    readonly Options opt;

    bool on, pinned, buttonsToo, stickToggle = true;
    int seconds = DEFAULT_SECONDS, toggleSeq = -1, countdownHands, lastBeep = -1, steps;
    DateTime countdownEnd, lastFast = DateTime.UtcNow;

    SkinButton switchButton;
    SkinLabel status, secondsLabel, headline, spotNote, carryNote;
    SkinPanel freezePanel, spotsPanel;
    SkinCheckBox buttonsBox, stickBox, pinBox;
    readonly SkinLabel[] rowState = new SkinLabel[3];
    readonly List<SpotRow> spotRows = new List<SpotRow>(); readonly List<SkinLabel> spotHeads = new List<SkinLabel>();
    List<Spot> shown = new List<Spot>(); string shownKey = null;

    static readonly string[] RowName = { "LEFT HAND", "RIGHT HAND", "BOTH HANDS" };
    static readonly int[] RowHands = { Hands.LEFT, Hands.RIGHT, Hands.BOTH };

    public class Options { public string Game, Shot; public bool Test; }

    public FreezeUi(Options options) {
        opt = options;
        settingsFile = Path.Combine(opt.Test && opt.Game != null ? opt.Game : AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
        carry = new HandCarry(shared) { ForcedGame = opt.Game };
        Text = "Hand Freeze";
        string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hand_freeze.ico");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; BackColor = Look.Back; ForeColor = Ink;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(LEFT_W + SPOTS_W, TOP_H + PAGE_H);
        Backdrop.Init(this, ico, Look.Back);
        if (!opt.Test) Layer.Mend();   // a stale registration is re-done here (v3.2)
        on = Layer.IsOn();
        LoadSettings();
        TopMost = pinned;
        Build();
        Step();

        tick.Interval = STEP_MS; tick.Tick += (s, e) => Step(); tick.Start();
        fast.Interval = FAST_MS; fast.Tick += (s, e) => Fast();
        FormClosing += (s, e) => { shared.Unfreeze(Hands.BOTH); shared.Tell(false, false); carry.Dispose(); shared.Dispose(); };
        if (opt.Shot != null) {   // tests: a picture of the window, then close
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(opt.Shot, ImageFormat.Png); } Close(); };
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
        var top = new SkinPanel { Dock = DockStyle.Top, Height = TOP_H, Tint = Color.FromArgb(150, Look.Back) };
        Put(top, Lbl("HAND FREEZE", 11f, Ink, 124, 32), 12, 6);
        switchButton = Put(top, Btn("OFF", 70, 32, Look.Bad, (s, e) => Toggle()), 140, 6);
        status = Put(top, Lbl("", 9.5f, Dim, 400, 32), 220, 6);

        freezePanel = new SkinPanel { Dock = DockStyle.Left, Width = LEFT_W };
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
        Put(freezePanel, new SkinLabel { ForeColor = Dim, Width = 616, Height = 40, Font = new Font("Segoe UI", 9f),
            Text = "RIGHT stick click: freeze / un-freeze both hands.   LEFT stick click: Window View's pointers, frozen or not.\nPointers out: hold a grip away from every window to carry the frozen hands (right stick turns, left stick pushes)." }, 12, 270);
        carryNote = Put(freezePanel, Lbl("", 10f, Look.Warn, 616, 24), 12, 314);
        MouseWheel += (s, e) => SetSeconds(seconds + Math.Sign(e.Delta));

        spotsPanel = new SkinPanel { Dock = DockStyle.Fill, Tint = Color.FromArgb(205, Look.Back) };   // the backdrop kept faint under the words
        headline = Put(spotsPanel, Lbl("", 12f, Ink, 300, 28), 10, 6);
        spotNote = Put(spotsPanel, Lbl("", 9f, Dim, SPOTS_W - 330, 28), 320, 6);
        Put(freezePanel, new SkinLabel { ForeColor = Dim, Width = 616, Height = 22, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft,
            Text = "SPOTS:  GREEN = the hand is there now     ● = has its own pose in Hand Poser     grey = none yet" }, 12, 344);

        Controls.Add(spotsPanel); Controls.Add(freezePanel); Controls.Add(top);
    }
    static void Set(Control c, string text) { if (c.Text != text) c.Text = text; }
    static void Tint(Control c, Color back) { if (c.BackColor != back) c.BackColor = back; }
    static void InkOf(Control c, Color ink) { if (c.ForeColor != ink) c.ForeColor = ink; }

    // ---- settings -----------------------------------------------------------------------------------------------------------------
    // lines 5 to 7 were v5's tab and the 3D gun's distance and height: no longer written, skipped when read
    void LoadSettings() {
        try {
            var l = File.ReadAllLines(settingsFile);
            int v;
            if (l.Length > 0 && int.TryParse(l[0].Trim(), out v) && v >= 0 && v <= MAX_SECONDS) seconds = v;
            pinned = l.Length > 1 && l[1].Trim() == "pinned";
            buttonsToo = l.Length > 2 && l[2].Trim() == "buttons";
            stickToggle = l.Length <= 3 || l[3].Trim() != "no stick";   // on unless switched off
        } catch (Exception) { }
    }
    void SaveSettings() {
        if (freezePanel == null) return;   // still being built
        try { File.WriteAllLines(settingsFile, new[] { seconds.ToString(), pinned ? "pinned" : "free", buttonsToo ? "buttons" : "hands only", stickToggle ? "stick" : "no stick" }); } catch (Exception) { }
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
        if (fast.Enabled != frozen) { lastFast = DateTime.UtcNow; fast.Enabled = frozen; if (!frozen) carry.Fast(0); }
        if (steps++ % SLOW_EVERY == 0) { carry.Slow(); RefreshSpots(); }

        Set(switchButton, on ? "ON" : "OFF"); Tint(switchButton, on ? Color.FromArgb(52, 140, 78) : Color.FromArgb(170, 70, 60));
        string hands = (shared.LeftSpaces > 0 ? "L" : "") + (shared.RightSpaces > 0 ? "R" : "");
        Set(status, !on ? "Switched off. Switch on before the game starts." : !shared.GameConnected ? "No game connected yet." : "Connected: " + shared.GameName + (hands.Length > 0 ? "  (" + hands + ")" : ""));
        Set(secondsLabel, seconds.ToString());
        for (int i = 0; i < 3; i++) {
            int h = RowHands[i];
            bool isFrozen = (shared.Frozen & h) == h, buttons = isFrozen && (shared.ButtonsFrozen & h) == h, counting = countdownHands == h;
            Set(rowState[i], buttons ? "FROZEN + BUTTONS" : isFrozen ? "FROZEN" : counting ? "HOLD STILL  " + Math.Max(1, (int)Math.Ceiling((countdownEnd - now).TotalSeconds)) : "");
            InkOf(rowState[i], isFrozen ? Look.Good : Look.Warn);
        }
        Set(carryNote, carry.Carrying ? "CARRYING THE FROZEN HANDS" : frozen && carry.PointersOut ? "Pointers out: a grip off every window carries the hands." : "");
    }
    void Fast() {
        var now = DateTime.UtcNow; double dt = Math.Min(0.1, (now - lastFast).TotalSeconds); lastFast = now;
        carry.Fast(dt);
    }

    // ---- the spots: built again only when the gun or its list changed; a row's colours only when they changed -------------------
    void RefreshSpots() {
        var list = carry.SpotList;
        string key = carry.Gun + "|";
        foreach (var s in list) key += s.Hand + s.Code + "=" + s.Part + (s.Saved ? "*" : "") + ";";
        if (key != shownKey) { shownKey = key; BuildSpots(list); }
        for (int i = 0; i < shown.Count; i++) {
            var s = shown[i]; var row = spotRows[i];
            bool here = s.Part == (s.Hand == "r" ? carry.RealRight : carry.RealLeft);
            Tint(row, here ? HereBack : s.Saved ? SavedBack : Color.Transparent);
            InkOf(row, here || s.Saved ? Ink : Dim);
        }
        Set(headline, carry.Game == null ? "SPOTS" : carry.Gun == "" ? "SPOTS: NO GUN IN HAND" : "SPOTS: " + Spots.GunWords(carry.Gun));
        Set(spotNote, carry.Game == null ? "No RE game running yet. The spots show once the game is up, with Hand Poser's script in it."
            : !carry.Answering ? "Hand Poser's script in the game is not answering: the last gun's spots stay up, nothing is lit."
            : "left hand: " + (carry.RealLeft == "" ? "no spot" : carry.RealLeft) + "      right hand: " + (carry.RealRight == "" ? "no spot" : carry.RealRight));
    }
    void BuildSpots(List<Spot> list) {
        spotsPanel.SuspendLayout();
        foreach (var c in spotRows) { spotsPanel.Controls.Remove(c); c.Dispose(); }
        foreach (var c in spotHeads) { spotsPanel.Controls.Remove(c); c.Dispose(); }
        spotRows.Clear(); spotHeads.Clear(); shown = new List<Spot>(list);
        var left = list.FindAll(s => s.Hand == "l"); var right = list.FindAll(s => s.Hand == "r");
        int colsL = Math.Max(1, (left.Count + ROWS_PER_COLUMN - 1) / ROWS_PER_COLUMN), colsR = Math.Max(1, (right.Count + ROWS_PER_COLUMN - 1) / ROWS_PER_COLUMN);
        int colW = (SPOTS_W - 20) / (colsL + colsR);
        if (list.Count > 0) {
            spotHeads.Add(Put(spotsPanel, Lbl("LEFT HAND", 9.5f, Dim, colW * colsL - 8, 22), 10, SPOTS_TOP - 24));
            spotHeads.Add(Put(spotsPanel, Lbl("RIGHT HAND", 9.5f, Dim, colW * colsR - 8, 22), 10 + colW * colsL, SPOTS_TOP - 24));
        }
        var rowFont = new Font("Segoe UI", 9f);
        shown.Clear();
        foreach (var side in new[] { left, right }) {
            int x0 = side == left ? 10 : 10 + colW * colsL;
            for (int i = 0; i < side.Count; i++) {
                var s = side[i];
                var row = new SpotRow { Code = s.Code, Dot = s.Saved, Words = s.Words, Font = rowFont, ForeColor = Dim, Width = colW - 8, Height = ROW_H - 2 };
                Put(spotsPanel, row, x0 + (i / ROWS_PER_COLUMN) * colW, SPOTS_TOP + (i % ROWS_PER_COLUMN) * ROW_H);
                spotRows.Add(row); shown.Add(s);
            }
        }
        spotsPanel.ResumeLayout();
    }
}
