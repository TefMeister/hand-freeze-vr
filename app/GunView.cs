// GunView.cs -- the rough, turnable picture of the gun on the SPOTS tab, with the hand spots hung on it (2026-11-25).
//
// Tefa: "make a rough 3d picture of the gun that is 360 rotatable and have these tabs ... placed where the hand gets placed".
// The gun is drawn from its own joints (each joint-to-parent piece a thick bar, far ones first and darker), the grip zones as
// rings, the right hand's controller as a small ring. Every spot the hand can be put on is a button in a stack beside the gun,
// tied by a line to its zone; a drag anywhere else turns the gun. STILL: it repaints only when turned, clicked, or the gun or
// the hands' spot changed (helper windows must not animate).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

class GunView : Control {
    const int BUTTON_H = 28, BUTTON_GAP = 3, STACK_GAP = 12, EDGE = 10, FOOT_H = 26;
    const double DRAG_DEG_PER_PX = 0.5;
    const double BAR_M = 0.034;              // a joint bar's thickness
    const double FILL = 0.42;                // the gun's reach as a share of the room between the two button columns
    const int COLUMNS_W = 560;               // what the two button columns take of the width, roughly
    static readonly Color FarSteel = Color.FromArgb(58, 64, 82), NearSteel = Color.FromArgb(176, 186, 212), Outline = Color.FromArgb(14, 16, 22);
    static readonly Color RealEdge = Color.FromArgb(120, 200, 120);
    static readonly Color[] ZoneInk = { Color.FromArgb(86, 196, 232), Color.FromArgb(240, 170, 80), Color.FromArgb(170, 130, 245), Color.FromArgb(120, 210, 140),
                                        Color.FromArgb(235, 120, 150), Color.FromArgb(220, 210, 110), Color.FromArgb(120, 160, 245), Color.FromArgb(200, 200, 200) };

    GunShape gun; List<Spot> spots = new List<Spot>();
    string pickCode = "", realL = "", realR = "", note = "";
    readonly List<KeyValuePair<Rectangle, Spot>> hits = new List<KeyValuePair<Rectangle, Spot>>();
    Spot hover, pressed; bool dragging; Point last;
    readonly Font small = new Font("Segoe UI Semibold", 9f), big = new Font("Segoe UI Semibold", 13f);

    public Quat View = Sides[0];
    public event Action<Spot> SpotClicked;
    public event Action Turned;
    // the six plain views: the gun's left side (muzzle to the picture's left), right side, from above, from below, muzzle-on, from behind
    public static readonly Quat[] Sides = {
        Quat.About(new V3(0, 1, 0), -90), Quat.About(new V3(0, 1, 0), 90),
        Quat.About(new V3(1, 0, 0), 90) * Quat.About(new V3(0, 1, 0), -90), Quat.About(new V3(1, 0, 0), -90) * Quat.About(new V3(0, 1, 0), -90),
        Quat.About(new V3(0, 1, 0), 0), Quat.About(new V3(0, 1, 0), 180),
    };

    public GunView() {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // what to show; repaints only when something differs
    public void Show(GunShape g, List<Spot> list, string picked, string realLeft, string realRight, string noteLine) {
        string was = Stamp();
        gun = g; spots = list ?? new List<Spot>(); pickCode = picked ?? ""; realL = realLeft ?? ""; realR = realRight ?? ""; note = noteLine ?? "";
        if (Stamp() != was) Invalidate();
    }
    string Stamp() {
        var sb = new System.Text.StringBuilder();
        sb.Append(gun == null ? "-" : gun.Gun + gun.Joints.Count + "/" + gun.Zones.Count + gun.HasGrip).Append('|').Append(pickCode).Append('|').Append(realL).Append('|').Append(realR).Append('|').Append(note);
        if (gun != null) foreach (var z in gun.Zones) sb.Append(z.Key).Append((int)(z.P.X * 500)).Append((int)(z.P.Y * 500)).Append((int)(z.P.Z * 500));
        foreach (var s in spots) sb.Append(s.Hand).Append(s.Code).Append(s.CanDo ? '+' : '-');
        return sb.ToString();
    }
    public void TurnBy(double yawDeg, double pitchDeg, double rollDeg) {
        View = (Quat.About(new V3(0, 0, 1), rollDeg) * Quat.About(new V3(1, 0, 0), pitchDeg) * Quat.About(new V3(0, 1, 0), yawDeg) * View).Unit;
        Invalidate(); var t = Turned; if (t != null) t();
    }
    public void SetView(Quat q) { View = q.Unit; Invalidate(); var t = Turned; if (t != null) t(); }

    // ---- the mouse (Window View's pointer sends the same messages) -----------------------------------------------------------
    Spot At(Point p) { foreach (var h in hits) if (h.Key.Contains(p)) return h.Value; return null; }
    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        pressed = At(e.Location); dragging = pressed == null; last = e.Location; Capture = true; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (dragging) {
            int dx = e.X - last.X, dy = e.Y - last.Y; last = e.Location;
            if (dx != 0 || dy != 0) TurnBy(dx * DRAG_DEG_PER_PX, dy * DRAG_DEG_PER_PX, 0);
            return;
        }
        var h = At(e.Location);
        if (h != hover) { hover = h; Cursor = h != null && h.CanDo ? Cursors.Hand : Cursors.SizeAll; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        Capture = false;
        var p = pressed; pressed = null; bool was = dragging; dragging = false;
        if (!was && p != null && At(e.Location) == p && p.CanDo) { var c = SpotClicked; if (c != null) c(p); }
        Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { if (hover != null) { hover = null; Invalidate(); } }

    // ---- drawing ---------------------------------------------------------------------------------------------------------------
    protected override void OnPaintBackground(PaintEventArgs e) {
        Backdrop.PaintUnder(this, e.Graphics, e.ClipRectangle);
        using (var b = new SolidBrush(Look.BoxTint)) e.Graphics.FillRectangle(b, e.ClipRectangle);
    }
    static Color Mix(Color a, Color b, double t) {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
    static Color InkOf(string key) { int h = 0; foreach (char c in key) h = h * 31 + c; return ZoneInk[(h & 0x7fffffff) % ZoneInk.Length]; }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        hits.Clear();
        int w = ClientSize.Width, h = ClientSize.Height - FOOT_H;
        if (gun == null || (gun.Joints.Count == 0 && gun.Zones.Count == 0)) {
            Centre(g, note != "" ? note : "No gun yet: hold a gun in the game and its picture comes up here.", w, h);
            return;
        }
        V3 mid = gun.Middle; double reach = gun.Reach, s = Math.Max(120, Math.Min(w - COLUMNS_W, h)) * FILL / reach;
        float cx = w / 2f, cy = h / 2f;
        Func<V3, V3> turn = p => View.Turn(p - mid);
        Func<V3, PointF> at = v => new PointF(cx + (float)(v.X * s), cy - (float)(v.Y * s));

        // the grip zones: a ring at their real size, behind the gun
        foreach (var z in gun.Zones) {
            var c = at(turn(z.P)); float r = (float)(z.R * s);
            using (var p = new Pen(Color.FromArgb(70, InkOf(z.Key)), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawEllipse(p, c.X - r, c.Y - r, 2 * r, 2 * r);
        }
        // the gun: every joint-to-parent piece as a thick bar, far ones first
        var bars = new List<double[]>();   // depth, x1, y1, x2, y2
        foreach (var j in gun.Joints) {
            if (j.Parent < 0 || j.Parent >= gun.Joints.Count || Hidden(j.Name) || Hidden(gun.Joints[j.Parent].Name)) continue;
            V3 a = turn(j.P), b = turn(gun.Joints[j.Parent].P);
            PointF pa = at(a), pb = at(b);
            bars.Add(new[] { (a.Z + b.Z) / 2, pa.X, pa.Y, pb.X, pb.Y });
        }
        bars.Sort((x, y) => x[0].CompareTo(y[0]));
        float bar = (float)Math.Max(7, BAR_M * s);
        foreach (var b in bars) {
            Color c = Mix(FarSteel, NearSteel, (b[0] + reach) / (2 * reach));
            using (var o = new Pen(Outline, bar + 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(o, (float)b[1], (float)b[2], (float)b[3], (float)b[4]);
            using (var p = new Pen(c, bar) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, (float)b[1], (float)b[2], (float)b[3], (float)b[4]);
        }
        // the muzzle's way and up, from the gun's middle, so a turned picture can still be read
        Arrow(g, at(turn(mid)), at(turn(mid + new V3(0, 0, reach * 0.9))), Color.FromArgb(150, 232, 232, 236), "MUZZLE");
        // the right hand's controller
        PointF hand = PointF.Empty; bool haveHand = gun.HasGrip;
        if (haveHand) {
            hand = at(turn(gun.Grip.Inverse.P));
            using (var p = new Pen(Look.Warn, 2f)) g.DrawEllipse(p, hand.X - 9, hand.Y - 9, 18, 18);
            using (var b = new SolidBrush(Look.Warn)) g.DrawString("RIGHT HAND", small, b, hand.X + 12, hand.Y - 8);
        }

        // the spots, in stacks left and right of the gun, each stack tied to its zone
        var stacks = new List<Stack>(); int cannot = 0;
        foreach (var sp in spots) {
            if (!sp.CanDo) { cannot++; continue; }
            PointF anchor; string key;
            if (sp.Hand == "r") { key = "(right hand)"; anchor = haveHand ? hand : at(turn(mid)); }
            else { var z = gun.Zone(sp.ZoneKey); key = sp.ZoneKey; anchor = at(turn(z.P)); }
            Stack st = stacks.Find(x => x.Key == key);
            if (st == null) { st = new Stack { Key = key, Anchor = anchor, Ink = sp.Hand == "r" ? Look.Warn : InkOf(key) }; stacks.Add(st); }
            st.Spots.Add(sp);
        }
        stacks.Sort((a, b) => a.Anchor.X.CompareTo(b.Anchor.X));
        for (int i = 0; i < stacks.Count; i++) stacks[i].Left = i < (stacks.Count + 1) / 2;   // the picture's left half of the zones to the left
        foreach (bool left in new[] { true, false }) {
            var col = stacks.FindAll(x => x.Left == left);
            col.Sort((a, b) => a.Anchor.Y.CompareTo(b.Anchor.Y));
            int colW = 0;
            foreach (var st in col) foreach (var sp in st.Spots) colW = Math.Max(colW, (int)g.MeasureString(sp.Code + "  " + sp.Words, small).Width + 16);
            int total = 0; foreach (var st in col) total += st.Spots.Count * (BUTTON_H + BUTTON_GAP) + STACK_GAP;
            int y = Math.Max(EDGE, (h - total) / 2);
            foreach (var st in col) {
                int x = left ? EDGE : w - EDGE - colW, top = y;
                foreach (var sp in st.Spots) {
                    var r = new Rectangle(x, y, colW, BUTTON_H);
                    DrawSpot(g, r, sp, st.Ink);
                    hits.Add(new KeyValuePair<Rectangle, Spot>(r, sp));
                    y += BUTTON_H + BUTTON_GAP;
                }
                float my = (top + y - BUTTON_GAP) / 2f, ex = left ? x + colW : x;
                using (var p = new Pen(Color.FromArgb(190, st.Ink), 2f)) g.DrawLine(p, ex, my, st.Anchor.X, st.Anchor.Y);
                using (var b = new SolidBrush(st.Ink)) g.FillEllipse(b, st.Anchor.X - 5, st.Anchor.Y - 5, 10, 10);
                y += STACK_GAP;
            }
        }
        string foot = note != "" ? note : cannot > 0 ? cannot + " more spots (holsters, pouch, map, first aid, the right hand off the grip) cannot be put up from here yet." : "";
        using (var b = new SolidBrush(note != "" ? Look.Warn : Look.Dim)) g.DrawString(foot, small, b, EDGE, h + 4);
    }
    class Stack { public string Key; public PointF Anchor; public Color Ink; public bool Left; public List<Spot> Spots = new List<Spot>(); }
    static bool Hidden(string joint) { return GunShape.Hidden(joint); }

    void DrawSpot(Graphics g, Rectangle r, Spot sp, Color ink) {
        bool picked = sp.Hand + sp.Code == pickCode, real = sp.Part == (sp.Hand == "r" ? realR : realL);
        Color back = picked ? Look.Accent : sp == hover ? Mix(Look.ButtonBack, Color.White, 0.15) : Look.ButtonBack;
        if (sp == pressed) back = Mix(back, Color.Black, 0.2);
        using (var b = new SolidBrush(back)) g.FillRectangle(b, r);
        using (var b = new SolidBrush(ink)) g.FillRectangle(b, r.X, r.Y, 4, r.Height);
        if (real) using (var p = new Pen(RealEdge, 2f)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
        using (var b = new SolidBrush(Look.Ink)) g.DrawString(sp.Code + "  " + sp.Words, small, b, r.X + 9, r.Y + 5);
    }
    void Arrow(Graphics g, PointF a, PointF b, Color c, string word) {
        if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 12) return;   // seen end-on
        using (var p = new Pen(c, 1.5f) { DashStyle = DashStyle.Dot, CustomEndCap = new AdjustableArrowCap(4, 5) }) g.DrawLine(p, a, b);
        using (var br = new SolidBrush(c)) g.DrawString(word, small, br, b.X + 4, b.Y - 8);
    }
    void Centre(Graphics g, string text, int w, int h) {
        var size = g.MeasureString(text, big, w - 40);
        using (var b = new SolidBrush(Look.Dim)) g.DrawString(text, big, b, new RectangleF((w - size.Width) / 2, (h - size.Height) / 2, size.Width + 2, size.Height + 2));
    }
}
