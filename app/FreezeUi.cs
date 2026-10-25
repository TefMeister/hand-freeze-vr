// FreezeUi.cs -- the Hand Freeze window, drawn by hand onto a small canvas and scaled up with hard pixel edges, so the
// whole window is pixel art (Tefa 2026-10-13: "if you can, make it look pixelated").
//
// Top: the title and a ROUND switch, red when OFF, green when ON. Behind everything a STILL picture: the app's own icon,
// pixelated and faint, the same ON and OFF (2026-10-25, Tefa: "please make these windows as little performance hungry as they
// can, we need to lose the animations and please make the hand freeze window also just have the icon on the background for
// both on and off"; until then a looping summer scene while OFF and a winter one while ON, summer.gif / winter.gif, and a
// flickering flame). The window is redrawn only when something on it changes. Then the seconds picker, then three
// rows, LEFT HAND, RIGHT HAND and BOTH HANDS, each with a snowflake-shaped FREEZE button and a flame-shaped UN- / FREEZE
// button. During a countdown the snowflake shows the seconds left; a frozen row's label turns icy. The font is Silkscreen
// (a pixel font, SIL OFL), embedded in the exe with the two backgrounds. It is drawn only at its own pixel sizes, 8 and
// 16, so every letter stays sharp and apart (Marcellus ran letters together at small sizes: Tefa, 2026-10-13).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class FreezeUi : Form {
    // ---- canvas and layout (canvas pixels; the window shows each one as SCALE x SCALE screen pixels) --------------
    const int CANVAS_W = 240, CANVAS_H = 356;   // must match make_backgrounds.py W, H
    const int HEADER_H = 74;
    const int SWITCH_X = 205, SWITCH_Y = 38, SWITCH_R = 19;
    const int SECONDS_Y = 90, BOX_H = 20, MINUS_X = 140, NUMBER_X = 160, NUMBER_W = 46, PLUS_X = 208, ARROW_W = 18;
    const int ROW_Y0 = 114, ROW_H = 72, LABEL_X = 10, FLAKE_X = 122, FLAME_X = 196, FLAKE_R = 28;
    const int FLAME_DROP = 5;                   // the flame sits a little low, so its tall tongues stay inside the row
    const int STATUS_Y = 339, PIN_W = 78;
    const int BUTTONS_X = 12, BUTTONS_Y = 60, BOX = 10;   // v3: the BUTTONS TOO tick box, in the header where the OFF hint sits
    const int BUTTONS_HIT_W = 86;
    const int STICK_X = 104, STICK_HIT_W = 128;          // v4: the R STICK CLICK tick box, beside it
    const int STEP_MS = 100;                // how often the shared block is looked at (the window is redrawn only on a change)
    const float PIC_STRENGTH = 0.30f;       // how faint the icon behind everything is
    const int DEFAULT_SECONDS = 10, MAX_SECONDS = 60;
    const int TICK_HZ = 880, TICK_MS = 70, FROZEN_HZ = 1320, FROZEN_MS = 350;   // the countdown beeps, the final one

    static readonly Color Red = Color.FromArgb(205, 60, 55), Green = Color.FromArgb(60, 185, 85);
    static readonly Color Ink = Color.FromArgb(20, 22, 34), Paper = Color.FromArgb(250, 250, 245);
    static readonly Color Ice = Color.FromArgb(170, 225, 255), IceDeep = Color.FromArgb(40, 110, 190);
    static readonly Color FlameOut = Color.FromArgb(225, 75, 35), FlameIn = Color.FromArgb(255, 200, 60), FlameEdge = Color.FromArgb(110, 25, 15);

    readonly Shared shared = new Shared();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly Bitmap canvas = new Bitmap(CANVAS_W, CANVAS_H, PixelFormat.Format32bppArgb);
    readonly Bitmap still;      // the background: the icon, pixelated and faint (2026-10-25)
    string shownState = "";     // what the last drawing showed; a redraw only when it changes
    readonly PrivateFontCollection fonts = new PrivateFontCollection();
    readonly FontFamily face;
    readonly int scale;
    readonly string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");

    bool on;
    float fade;                 // 0 = red (OFF), 1 = green (ON); no longer eased (no animation)
    int seconds = DEFAULT_SECONDS;
    bool pinned;
    bool buttonsToo;            // v3: a freeze also holds the buttons, triggers, grips and sticks of those hands
    bool stickToggle = true;    // v4: the right stick click freezes / unfreezes both hands (and never reaches the game)
    int toggleSeq = -1;
    int countdownHands;         // which hands the running countdown will freeze; 0 = none
    DateTime countdownEnd;
    int lastBeep = -1;
    string hover = "", pressed = "";

    struct Row { public string Label, Noun; public int Hands; public int Y; }
    static readonly Row[] Rows = {
        new Row { Label = "LEFT", Noun = "HAND", Hands = Hands.LEFT, Y = ROW_Y0 },
        new Row { Label = "RIGHT", Noun = "HAND", Hands = Hands.RIGHT, Y = ROW_Y0 + ROW_H },
        new Row { Label = "BOTH", Noun = "HANDS", Hands = Hands.BOTH, Y = ROW_Y0 + 2 * ROW_H },
    };

    public FreezeUi() {
        Text = "Hand Freeze";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        DoubleBuffered = true;
        using (var g = CreateGraphics()) scale = Math.Max(2, (int)Math.Round(2 * g.DpiX / 96f));
        ClientSize = new Size(CANVAS_W * scale, CANVAS_H * scale);

        face = LoadFont("Silkscreen-Regular.ttf");
        still = MakeStill();
        Layer.Mend();   // v3.2: a stale registration (ON pressed before v3.1 was installed) is re-done here
        on = Layer.IsOn();
        fade = on ? 1 : 0;
        LoadSettings();
        TopMost = pinned;

        tick.Interval = STEP_MS;
        tick.Tick += (s, e) => Step();
        tick.Start();
        MouseMove += (s, e) => { string h = HitTest(e.Location); if (h != hover) { hover = h; Cursor = h != "" ? Cursors.Hand : Cursors.Default; Invalidate(); } };
        MouseLeave += (s, e) => { hover = ""; Invalidate(); };
        MouseDown += (s, e) => { pressed = HitTest(e.Location); Invalidate(); };
        MouseUp += (s, e) => { string h = HitTest(e.Location); if (h != "" && h == pressed) Press(h); pressed = ""; Invalidate(); };
        MouseWheel += (s, e) => { if (on) SetSeconds(seconds + Math.Sign(e.Delta)); };
        FormClosing += (s, e) => { shared.Unfreeze(Hands.BOTH); shared.Tell(false, false); shared.Dispose(); };
    }

    // ---- resources -------------------------------------------------------------------------------------------------
    static Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }

    FontFamily LoadFont(string name) {
        try {
            using (var s = Resource(name)) {
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                IntPtr mem = Marshal.AllocCoTaskMem(bytes.Length);   // GDI+ keeps reading it: never freed, on purpose
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                fonts.AddMemoryFont(mem, bytes.Length);
                return fonts.Families[0];
            }
        } catch { return new FontFamily("Candara"); }
    }

    // the background, made once: a dark field with the app's icon on it, pixelated (the canvas is shown with hard pixel edges)
    // and faint. The icon's big sizes are PNG inside the .ico, which .NET's Icon class draws blank, so the PNG is read directly
    static Bitmap MakeStill() {
        var b = new Bitmap(CANVAS_W, CANVAS_H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) {
            g.Clear(Color.FromArgb(22, 28, 44));
            Bitmap pic = null;
            try { pic = LargestPicture(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hand_freeze.ico")); } catch { }
            if (pic == null) try { pic = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            if (pic != null) {
                int side = CANVAS_W - 20;
                var cm = new ColorMatrix { Matrix33 = PIC_STRENGTH };
                using (var ia = new ImageAttributes()) {
                    ia.SetColorMatrix(cm);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(pic, new Rectangle((CANVAS_W - side) / 2, (CANVAS_H - side) / 2 + 20, side, side), 0, 0, pic.Width, pic.Height, GraphicsUnit.Pixel, ia);
                }
                pic.Dispose();
            }
        }
        return b;
    }
    static Bitmap LargestPicture(string path) {
        byte[] f = File.ReadAllBytes(path);
        int count = BitConverter.ToUInt16(f, 4), best = -1, bestSide = 0;
        for (int i = 0; i < count; i++) { int e = 6 + 16 * i, side = f[e] == 0 ? 256 : f[e]; if (side > bestSide) { bestSide = side; best = e; } }
        if (best < 0) return null;
        int size = BitConverter.ToInt32(f, best + 8), at = BitConverter.ToInt32(f, best + 12);
        bool png = f[at] == 0x89 && f[at + 1] == 0x50 && f[at + 2] == 0x4E && f[at + 3] == 0x47;
        if (png) using (var ms = new MemoryStream(f, at, size)) using (var img = Image.FromStream(ms)) return new Bitmap(img);
        using (var ico = new Icon(path, bestSide, bestSide)) return ico.ToBitmap();
    }


    void LoadSettings() {
        try {
            var lines = File.ReadAllLines(settingsFile);
            int v;
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out v) && v >= 0 && v <= MAX_SECONDS) seconds = v;
            pinned = lines.Length > 1 && lines[1].Trim() == "pinned";
            buttonsToo = lines.Length > 2 && lines[2].Trim() == "buttons";
            stickToggle = lines.Length <= 3 || lines[3].Trim() != "no stick";   // on unless switched off
        } catch { }
    }
    void SaveSettings() { try { File.WriteAllLines(settingsFile, new[] { seconds.ToString(), pinned ? "pinned" : "free", buttonsToo ? "buttons" : "hands only", stickToggle ? "stick" : "no stick" }); } catch { } }

    // ---- behaviour -------------------------------------------------------------------------------------------------
    void Step() {
        var now = DateTime.Now;
        fade = on ? 1 : 0;
        shared.Tell(on && stickToggle, buttonsToo);   // v4: the layer acts on the click only while this is fresh
        int seq = shared.ToggleSeq;
        if (toggleSeq >= 0 && seq != toggleSeq) { countdownHands = 0; Beep(shared.Frozen != 0 ? FROZEN_HZ : TICK_HZ, shared.Frozen != 0 ? FROZEN_MS : TICK_MS * 2); }
        toggleSeq = seq;
        if (countdownHands != 0) {
            double left = (countdownEnd - now).TotalSeconds;
            if (left <= 0) {
                shared.Freeze(countdownHands, buttonsToo);
                countdownHands = 0;
                Beep(FROZEN_HZ, FROZEN_MS);
            } else {
                int whole = (int)Math.Ceiling(left);
                if (whole != lastBeep) { lastBeep = whole; Beep(TICK_HZ, TICK_MS); }
            }
        }
        // redraw only when what the window shows has changed (the countdown's number, a frozen row, the game line, a tick box)
        int secsLeft = countdownHands != 0 ? Math.Max(1, (int)Math.Ceiling((countdownEnd - now).TotalSeconds)) : 0;
        string state = string.Join("|", on, countdownHands, secsLeft, shared.Frozen, shared.ButtonsFrozen, shared.GameConnected,
            shared.GameConnected ? shared.GameName : "", shared.LeftSpaces > 0, shared.RightSpaces > 0, buttonsToo, stickToggle, seconds, pinned);
        if (state != shownState) { shownState = state; Invalidate(); }
    }

    void Press(string what) {
        if (what == "switch") { Toggle(); return; }
        if (what == "pin") { pinned = !pinned; TopMost = pinned; SaveSettings(); return; }
        if (!on) return;
        if (what == "buttons") { buttonsToo = !buttonsToo; SaveSettings(); return; }
        if (what == "stick") { stickToggle = !stickToggle; SaveSettings(); return; }
        if (what == "minus") { SetSeconds(seconds - 1); return; }
        if (what == "plus") { SetSeconds(seconds + 1); return; }
        foreach (var r in Rows) {
            if (what == "freeze" + r.Hands) {
                countdownHands = r.Hands;
                countdownEnd = DateTime.Now.AddSeconds(seconds);
                lastBeep = -1;
                if (seconds == 0) Step();
            } else if (what == "unfreeze" + r.Hands) {
                if ((countdownHands & r.Hands) != 0) countdownHands = 0;
                shared.Unfreeze(r.Hands);
            }
        }
    }

    void Toggle() {
        try {
            if (!on) {
                if (!File.Exists(Layer.Dll)) { MessageBox.Show("hand_freeze_layer.dll is missing from this folder:\n" + Layer.Dll, "Hand Freeze"); return; }
                Layer.TurnOn(); on = true;
            } else {
                countdownHands = 0; shared.Unfreeze(Hands.BOTH); Layer.TurnOff(); on = false;
            }
        } catch (Exception ex) { MessageBox.Show("Could not switch: " + ex.Message, "Hand Freeze"); }
    }

    void SetSeconds(int v) { seconds = Math.Max(0, Math.Min(MAX_SECONDS, v)); SaveSettings(); }

    static void Beep(int hz, int ms) { ThreadPool.QueueUserWorkItem(_ => { try { Console.Beep(hz, ms); } catch { } }); }

    // ---- hit testing (canvas coordinates) --------------------------------------------------------------------------
    string HitTest(Point screen) {
        float x = screen.X / (float)scale, y = screen.Y / (float)scale;
        if (Dist(x, y, SWITCH_X, SWITCH_Y) <= SWITCH_R) return "switch";
        if (new RectangleF(CANVAS_W - 6 - PIN_W, STATUS_Y - 4, PIN_W, 15).Contains(x, y)) return "pin";
        if (on && new RectangleF(BUTTONS_X - 2, BUTTONS_Y - 3, BUTTONS_HIT_W, 16).Contains(x, y)) return "buttons";
        if (on && new RectangleF(STICK_X - 2, BUTTONS_Y - 3, STICK_HIT_W, 16).Contains(x, y)) return "stick";
        if (new RectangleF(MINUS_X, SECONDS_Y - 3, ARROW_W, BOX_H).Contains(x, y)) return "minus";
        if (new RectangleF(PLUS_X, SECONDS_Y - 3, ARROW_W, BOX_H).Contains(x, y)) return "plus";
        foreach (var r in Rows) {
            int cy = r.Y + ROW_H / 2 - 2;
            if (Dist(x, y, FLAKE_X, cy) <= FLAKE_R) return "freeze" + r.Hands;
            if (new RectangleF(FLAME_X - 24, cy - 24, 48, 56).Contains(x, y)) return "unfreeze" + r.Hands;
        }
        return "";
    }
    static float Dist(float x, float y, float cx, float cy) { return (float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)); }

    // ---- drawing ---------------------------------------------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e) {
        using (var g = Graphics.FromImage(canvas)) {
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;   // hard-edged pixel letters
            g.PixelOffsetMode = PixelOffsetMode.Half;
            DrawBackground(g);
            DrawHeader(g);
            DrawSeconds(g);
            foreach (var r in Rows) DrawRow(g, r);
            DrawStatus(g);
        }
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(canvas, new Rectangle(0, 0, CANVAS_W * scale, CANVAS_H * scale));
    }

    void DrawBackground(Graphics g) {
        g.DrawImage(still, 0, 0, CANVAS_W, CANVAS_H);
    }

    static Color Mix(Color a, Color b, float t) {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
    static Color Lighter(Color c, float t) { return Mix(c, Color.White, t); }
    static Color Grey(Color c) { int v = (c.R + c.G + c.B) / 3; return Mix(Color.FromArgb(v, v, v), Color.FromArgb(120, 120, 125), 0.4f); }

    const float SMALL = 8, BIG = 16;   // Silkscreen's own pixel sizes; anything in between blurs the letters

    Font F(float px) { return new Font(face, px, FontStyle.Regular, GraphicsUnit.Pixel); }

    // text with a one-pixel dark outline, so it reads on any background
    void Say(Graphics g, string text, float px, float x, float y, Color color, bool centre = false) {
        using (var f = F(px))
        using (var b = new SolidBrush(color))
        using (var o = new SolidBrush(Ink)) {
            if (centre) x -= g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width / 2;
            var fmt = StringFormat.GenericTypographic;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (dx != 0 || dy != 0) g.DrawString(text, f, o, x + dx, y + dy, fmt);
            g.DrawString(text, f, b, x, y, fmt);
        }
    }

    void Panel(Graphics g, int x, int y, int w, int h) {
        using (var b = new SolidBrush(Color.FromArgb(120, Ink))) g.FillRectangle(b, x, y, w, h);
        using (var p = new Pen(Color.FromArgb(160, Paper))) g.DrawRectangle(p, x, y, w - 1, h - 1);
    }

    void DrawHeader(Graphics g) {
        Panel(g, 4, 4, CANVAS_W - 8, HEADER_H);
        Say(g, "HAND", BIG, 12, 9, Paper);
        Say(g, "FREEZE", BIG, 12, 26, Lighter(Ice, 0.3f));
        if (!on) Say(g, "Switch on before the game starts.", SMALL, 12, 63, Paper);   // the one hint (Tefa, 2026-10-13)
        else {   // v3: the tick box takes the hint's place while on
            Color tickWord = hover == "buttons" ? Lighter(Ice, 0.4f) : Paper;
            using (var b = new SolidBrush(Ink)) g.FillRectangle(b, BUTTONS_X, BUTTONS_Y, BOX, BOX);
            using (var p = new Pen(tickWord)) g.DrawRectangle(p, BUTTONS_X, BUTTONS_Y, BOX - 1, BOX - 1);
            if (buttonsToo) using (var b = new SolidBrush(Green)) g.FillRectangle(b, BUTTONS_X + 2, BUTTONS_Y + 2, BOX - 4, BOX - 4);
            Say(g, "BUTTONS TOO", SMALL, BUTTONS_X + BOX + 5, BUTTONS_Y + 1, tickWord);
            // v4: the right stick click (Tefa 2026-10-25)
            Color stickWord = hover == "stick" ? Lighter(Ice, 0.4f) : Paper;
            using (var b = new SolidBrush(Ink)) g.FillRectangle(b, STICK_X, BUTTONS_Y, BOX, BOX);
            using (var p = new Pen(stickWord)) g.DrawRectangle(p, STICK_X, BUTTONS_Y, BOX - 1, BOX - 1);
            if (stickToggle) using (var b = new SolidBrush(Green)) g.FillRectangle(b, STICK_X + 2, BUTTONS_Y + 2, BOX - 4, BOX - 4);
            Say(g, "R STICK CLICK", SMALL, STICK_X + BOX + 5, BUTTONS_Y + 1, stickWord);
        }
        // the round switch
        Color c = Mix(Red, Green, fade);
        bool hot = hover == "switch", down = pressed == "switch";
        int r = SWITCH_R, cx = SWITCH_X, cy = SWITCH_Y + (down ? 1 : 0);
        using (var b = new SolidBrush(Ink)) g.FillEllipse(b, cx - r - 2, cy - r - 2, 2 * r + 4, 2 * r + 4);
        using (var b = new SolidBrush(Mix(c, Ink, 0.35f))) g.FillEllipse(b, cx - r, cy - r + 2, 2 * r, 2 * r);   // rim
        using (var b = new SolidBrush(hot ? Lighter(c, 0.15f) : c)) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r - 2);
        using (var b = new SolidBrush(Lighter(c, 0.45f))) g.FillEllipse(b, cx - r + 5, cy - r + 3, r - 2, r / 2);     // shine
        Say(g, fade >= 0.5f ? "ON" : "OFF", BIG, cx, cy - 12, Paper, true);
    }

    void DrawSeconds(Graphics g) {
        Panel(g, 4, SECONDS_Y - 7, CANVAS_W - 8, BOX_H + 8);
        Say(g, "SECONDS TO FREEZE", SMALL, 12, SECONDS_Y + 3, on ? Paper : Grey(Paper));
        DrawArrowBox(g, MINUS_X, "minus", "<");
        using (var b = new SolidBrush(Color.FromArgb(200, Ink))) g.FillRectangle(b, NUMBER_X, SECONDS_Y - 3, NUMBER_W, BOX_H);
        Say(g, seconds.ToString(), BIG, NUMBER_X + NUMBER_W / 2, SECONDS_Y - 6, on ? Paper : Grey(Paper), true);
        DrawArrowBox(g, PLUS_X, "plus", ">");
    }

    void DrawArrowBox(Graphics g, int x, string id, string arrow) {
        Color c = !on ? Grey(IceDeep) : hover == id ? Lighter(IceDeep, 0.25f) : IceDeep;
        int y = SECONDS_Y - 3 + (pressed == id ? 1 : 0);
        using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, ARROW_W - 1, BOX_H - 1);   // same size as the outline, so no colour leaks past it
        using (var p = new Pen(Ink)) g.DrawRectangle(p, x, y, ARROW_W - 1, BOX_H - 1);
        Say(g, arrow, BIG, x + ARROW_W / 2, y - 3, Paper, true);
    }

    void DrawRow(Graphics g, Row r) {
        int cy = r.Y + ROW_H / 2 - 2;
        bool frozen = (shared.Frozen & r.Hands) == r.Hands;
        bool buttons = frozen && (shared.ButtonsFrozen & r.Hands) == r.Hands;
        bool counting = countdownHands == r.Hands;
        Panel(g, 4, r.Y, CANVAS_W - 8, ROW_H - 4);
        if (frozen) using (var b = new SolidBrush(Color.FromArgb(90, Ice))) g.FillRectangle(b, 5, r.Y + 1, CANVAS_W - 10, ROW_H - 6);
        Color word = frozen ? Ice : (on ? Paper : Grey(Paper));
        Say(g, r.Label, BIG, LABEL_X, cy - 24, word);   // two lines: LEFT / HAND, so the words stay big and clear
        Say(g, r.Noun, BIG, LABEL_X, cy - 9, word);
        Say(g, buttons ? "FROZEN+BUTTONS" : frozen ? "FROZEN" : counting ? "HOLD STILL" : "", SMALL, LABEL_X, cy + 10, frozen ? Ice : FlameIn);
        string flakeId = "freeze" + r.Hands, flameId = "unfreeze" + r.Hands;
        DrawSnowflake(g, FLAKE_X, cy + (pressed == flakeId ? 1 : 0), hover == flakeId);
        string inside = counting ? Math.Max(1, (int)Math.Ceiling((countdownEnd - DateTime.Now).TotalSeconds)).ToString() : "FREEZE";
        Say(g, inside, counting ? BIG : SMALL, FLAKE_X, cy - (counting ? 8 : 4) + (pressed == flakeId ? 1 : 0), Paper, true);
        DrawFlame(g, FLAME_X, cy + (pressed == flameId ? 1 : 0), hover == flameId);
        int down = pressed == flameId ? 1 : 0;   // two lines, UN- over FREEZE: fits the flame's shape (Tefa, 2026-10-13)
        Say(g, "UN-", SMALL, FLAME_X, cy + 1 + down, Paper, true);
        Say(g, "FREEZE", SMALL, FLAME_X, cy + 10 + down, Paper, true);
    }

    void DrawSnowflake(Graphics g, int cx, int cy, bool hot) {
        Color body = !on ? Grey(Ice) : hot ? Lighter(Ice, 0.4f) : Ice, deep = !on ? Grey(IceDeep) : IceDeep;
        for (int pass = 0; pass < 2; pass++) {   // a dark outline pass, then the ice on top
            using (var pen = new Pen(pass == 0 ? Ink : body, pass == 0 ? 6 : 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                for (int k = 0; k < 6; k++) {
                    double a = k * Math.PI / 3;   // arms left and right, so the flakes of neighbouring rows never touch
                    float ex = cx + (float)(FLAKE_R * Math.Cos(a)), ey = cy + (float)(FLAKE_R * Math.Sin(a));
                    g.DrawLine(pen, cx, cy, ex, ey);
                    // two pairs of side branches leaning outwards, a long pair and a short one near the tip: a real flake
                    for (int pair = 0; pair < 2; pair++) {
                        double at = pair == 0 ? 0.66 : 0.86, len = pair == 0 ? 8.0 : 4.5;
                        float mx = cx + (float)(FLAKE_R * at * Math.Cos(a)), my = cy + (float)(FLAKE_R * at * Math.Sin(a));
                        foreach (int side in new[] { -1, 1 }) {
                            double b = a + side * Math.PI / 3;
                            g.DrawLine(pen, mx, my, mx + (float)(len * Math.Cos(b)), my + (float)(len * Math.Sin(b)));
                        }
                    }
                }
            }
            int r = pass == 0 ? 20 : 18;   // wide enough for the word FREEZE
            using (var b = new SolidBrush(pass == 0 ? Ink : deep)) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
        }
        using (var b = new SolidBrush(Lighter(deep, 0.25f))) g.FillEllipse(b, cx - 14, cy - 16, 24, 10);   // a little shine
    }

    void DrawFlame(Graphics g, int cx, int cy, bool hot) {
        Color outC = !on ? Grey(FlameOut) : hot ? Lighter(FlameOut, 0.2f) : FlameOut;
        Color inC = !on ? Grey(FlameIn) : hot ? Lighter(FlameIn, 0.2f) : FlameIn;
        Color edge = !on ? Grey(FlameEdge) : FlameEdge;
        float flick = 0;   // 2026-10-25: the flame stands still (no animation)
        using (var outer = FlamePath(cx, cy + FLAME_DROP, 23, 26, flick))
        using (var inner = FlamePath(cx, cy + FLAME_DROP + 6, 14, 17, -flick)) {
            using (var p = new Pen(edge, 3)) g.DrawPath(p, outer);
            using (var b = new SolidBrush(outC)) g.FillPath(b, outer);
            using (var b = new SolidBrush(inC)) g.FillPath(b, inner);
        }
        using (var b = new SolidBrush(Color.FromArgb(150, edge))) g.FillRectangle(b, cx - 20, cy, 40, 19);   // a band for the two words
    }

    // a flame: round at the bottom, three tongues at the top, the middle one tallest; `flick` sways the tips
    static GraphicsPath FlamePath(float cx, float cy, float w, float h, float flick) {
        var p = new GraphicsPath();
        float bottom = cy + h * 0.75f, top = cy - h;
        p.AddBezier(cx, bottom, cx - w * 1.1f, bottom, cx - w, cy, cx - w * 0.7f + flick, cy - h * 0.45f);
        p.AddBezier(cx - w * 0.7f + flick, cy - h * 0.45f, cx - w * 0.45f, cy - h * 0.1f, cx - w * 0.35f, cy - h * 0.2f, cx - w * 0.3f + flick, cy - h * 0.65f);
        p.AddBezier(cx - w * 0.3f + flick, cy - h * 0.65f, cx - w * 0.15f, cy - h * 0.35f, cx - w * 0.1f, cy - h * 0.6f, cx + flick * 1.5f, top);
        p.AddBezier(cx + flick * 1.5f, top, cx + w * 0.15f, cy - h * 0.6f, cx + w * 0.2f, cy - h * 0.35f, cx + w * 0.35f + flick, cy - h * 0.6f);
        p.AddBezier(cx + w * 0.35f + flick, cy - h * 0.6f, cx + w * 0.4f, cy - h * 0.2f, cx + w * 0.5f, cy - h * 0.1f, cx + w * 0.7f + flick, cy - h * 0.4f);
        p.AddBezier(cx + w * 0.7f + flick, cy - h * 0.4f, cx + w, cy, cx + w * 1.1f, bottom, cx, bottom);
        p.CloseFigure();
        return p;
    }

    void DrawStatus(Graphics g) {
        Panel(g, 4, STATUS_Y - 6, CANVAS_W - 8, 18);
        string text;
        if (!on) text = "Switched off.";
        else if (!shared.GameConnected) text = "No game connected yet.";
        else {
            string hands = (shared.LeftSpaces > 0 ? "L" : "") + (shared.RightSpaces > 0 ? "R" : "");
            text = "Connected: " + shared.GameName + (hands.Length > 0 ? "  (" + hands + ")" : "");
        }
        Say(g, text, SMALL, 10, STATUS_Y - 1, Paper);
        Color pc = pinned ? Green : (hover == "pin" ? Lighter(IceDeep, 0.25f) : IceDeep);
        int px = CANVAS_W - 6 - PIN_W;
        using (var b = new SolidBrush(pc)) g.FillRectangle(b, px, STATUS_Y - 4, PIN_W - 1, 14);
        using (var p = new Pen(Ink)) g.DrawRectangle(p, px, STATUS_Y - 4, PIN_W - 1, 14);
        Say(g, "ALWAYS ON TOP", SMALL, px + PIN_W / 2, STATUS_Y - 1, Paper, true);
    }
}
