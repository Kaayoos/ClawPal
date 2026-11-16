using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClawPal.Companion;

/* ClawPal's one look on this PC: the orange of the printed case around the display's black screen.
   It never follows the look chosen for the display; previews of the display draw that look themselves. */
internal static class ClawTheme
{
    // The case. Everything written on it is near-black.
    internal static readonly Color Case = Color.FromArgb(0xF2, 0x6B, 0x2A);
    internal static readonly Color CaseHover = Color.FromArgb(0xE2, 0x5E, 0x21);
    internal static readonly Color CaseInk = Color.FromArgb(0x16, 0x12, 0x10);
    // The screen: the display's black, one tone step up for anything filled, and its warm white text.
    internal static readonly Color Screen = Color.FromArgb(0x10, 0x10, 0x10);
    internal static readonly Color Surface = Color.FromArgb(0x1E, 0x1C, 0x1A);
    internal static readonly Color Raised = Color.FromArgb(0x2C, 0x29, 0x26);
    internal static readonly Color Pressed = Color.FromArgb(0x38, 0x34, 0x30);
    internal static readonly Color Ink = Color.FromArgb(0xF5, 0xF1, 0xEB);
    internal static readonly Color Muted = Color.FromArgb(0xAE, 0xA6, 0x9D);
    internal static readonly Color Faint = Color.FromArgb(0x74, 0x6D, 0x66);
    // The one accent is the case itself: the action, the selection, the live state.
    internal static readonly Color Accent = Case;
    internal static readonly Color AccentHover = Color.FromArgb(0xFF, 0x80, 0x45);
    internal static readonly Color AccentPressed = Color.FromArgb(0xD5, 0x57, 0x1C);
    internal static readonly Color Warning = Color.FromArgb(0xFF, 0xBD, 0x55);
    // Flat fields from the crab's own palette, for the few modules that carry a colour of their own.
    internal static readonly Color Peach = Color.FromArgb(0xFF, 0xB3, 0x8B);
    internal static readonly Color Gold = Color.FromArgb(0xFF, 0xBD, 0x55);
    internal static readonly Color Sky = Color.FromArgb(0x44, 0x8E, 0xB9);
    internal static readonly Color Metal = Color.FromArgb(0xB5, 0xBA, 0xC4);
    internal static readonly Color WeatherApp = Color.FromArgb(0x55, 0xD5, 0xEC);

    internal static bool OnCase(Control control) => control.Parent?.BackColor.ToArgb() is { } back && (back == Case.ToArgb() || back == CaseHover.ToArgb());
    /* Inside a filled panel the surface is the ground, so fields sink to the screen and buttons rise. */
    internal static bool OnSurface(Control control) => control.Parent?.BackColor.ToArgb() == Surface.ToArgb();
    internal static Color Field(Control control) => OnSurface(control) ? Screen : Surface;
    internal static Color Mix(Color a, Color b, float amount) => Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

    // Two voices: Segoe UI Black for what the page shouts, Segoe UI for everything it says.
    // Fonts are shared; never dispose one returned here.
    private static readonly Dictionary<(string, float, FontStyle), Font> fonts = new();
    private static Font Cached(string family, float size, FontStyle style = FontStyle.Regular)
    {
        lock (fonts)
        {
            if (fonts.TryGetValue((family, size, style), out var font)) return font;
            font = new Font(family, size, style);
            // Systems without the face get the nearest Segoe UI weight instead of GDI's fallback.
            if (!font.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) { font.Dispose(); font = new Font("Segoe UI", size, family.Contains("Black") ? FontStyle.Bold : style); }
            return fonts[(family, size, style)] = font;
        }
    }
    internal static Font Display(float size) => Cached("Segoe UI Black", size);
    internal static Font Font(float size = 11, bool semibold = false) => semibold ? Cached("Segoe UI Semibold", size) : Cached("Segoe UI", size);
    internal const float Title = 30, Head = 15, Lead = 12, Body = 11, Small = 10;

    internal static GraphicsPath Round(RectangleF r, float radius = 10)
    {
        var path = new GraphicsPath(); if (radius <= 0) { path.AddRectangle(r); return path; }
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    /* Square by default: the interface is drawn in whole pixels, like the crab. */
    internal static void Fill(Graphics g, Color color, RectangleF r, float radius = 0)
    {
        using var brush = new SolidBrush(color);
        if (radius <= 0) { var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None; g.FillRectangle(brush, r); g.SmoothingMode = mode; return; }
        using var path = Round(r, radius); g.FillPath(brush, path);
    }
    /* A 2-pixel frame inside the bounds, for keyboard focus and the item being dragged. */
    internal static void Frame(Graphics g, Color color, Rectangle r, int width = 2)
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, r.X, r.Y, r.Width, width); g.FillRectangle(brush, r.X, r.Bottom - width, r.Width, width);
        g.FillRectangle(brush, r.X, r.Y, width, r.Height); g.FillRectangle(brush, r.Right - width, r.Y, width, r.Height);
    }
    internal static void Text(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis) =>
        TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPrefix);
    /* A tick drawn in square strokes, as the display draws its own. */
    internal static void Tick(Graphics g, Color color, PointF centre, float size, float weight = 2.4f)
    {
        var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, weight) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter };
        float u = size / 12f;
        g.DrawLines(pen, new[] { new PointF(centre.X - 5 * u, centre.Y), new PointF(centre.X - 1.5f * u, centre.Y + 3.5f * u), new PointF(centre.X + 5 * u, centre.Y - 3.5f * u) });
        g.SmoothingMode = mode;
    }
    internal static Label Label(string text, int width = 600, float size = Body, bool bold = false, int height = 30) =>
        new() { Text = text, Width = width, Height = height, ForeColor = bold ? Ink : Muted, Font = Font(size, bold), UseMnemonic = false };
    internal static ClawTextField Input(string placeholder = "") => new() { PlaceholderText = placeholder };
    /* Native menus wear the case's colours too. */
    internal static void Apply(Form form)
    {
        foreach (Control child in form.Controls) ApplyControl(child);
    }
    private static void ApplyControl(Control c)
    {
        if (c is Label label) label.UseMnemonic = false;
        if (c is Button b) { b.UseMnemonic = false; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; }
        foreach (Control child in c.Controls) ApplyControl(child);
    }
    /* Windows' "Show animations" setting; optional motion stops when it is off, and for still renders. */
    internal static bool Motion => !Still && SystemInformation.IsMenuAnimationEnabled && ClientAnimations();
    internal static bool Still { get; set; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int action, int param, out bool value, int ini);
    private static bool ClientAnimations() { try { return !SystemParametersInfo(0x1042, 0, out bool on, 0) || on; } catch (EntryPointNotFoundException) { return true; } }
}

internal sealed class ClawMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => ClawTheme.Surface;
    public override Color ImageMarginGradientBegin => ClawTheme.Surface;
    public override Color ImageMarginGradientMiddle => ClawTheme.Surface;
    public override Color ImageMarginGradientEnd => ClawTheme.Surface;
    public override Color MenuItemSelected => ClawTheme.Raised;
    public override Color MenuItemSelectedGradientBegin => ClawTheme.Raised;
    public override Color MenuItemSelectedGradientEnd => ClawTheme.Raised;
    public override Color MenuItemBorder => ClawTheme.Raised;
    public override Color MenuBorder => ClawTheme.Raised;
    public override Color SeparatorDark => ClawTheme.Raised;
    public override Color SeparatorLight => ClawTheme.Raised;
}

/* A flat square button. On the screen: a filled block, orange when it is the page's action or the
   chosen option. On the case: a black block with warm white text. */
internal class ClawButton : Button
{
    internal bool Primary { get; set; }
    internal bool Selected { get; set; }
    /* Drawn over black painted by its parent (the welcome strip) rather than over the case. */
    internal bool OnScreen { get; set; }
    private bool hovered, pressed;
    public ClawButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; UseMnemonic = false; Cursor = Cursors.Hand;
        Font = ClawTheme.Font(ClawTheme.Body, true); Size = new(155, 44);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected bool Hot => hovered && Enabled;
    protected bool Down => pressed && Enabled;
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    internal (Color Fill, Color Ink) Colors()
    {
        if (!OnScreen && ClawTheme.OnCase(this))
        {
            if (!Enabled) return (ClawTheme.Mix(ClawTheme.Case, ClawTheme.CaseInk, .18f), ClawTheme.Mix(ClawTheme.CaseInk, ClawTheme.Case, .45f));
            return (Down ? ClawTheme.Mix(ClawTheme.CaseInk, ClawTheme.Ink, .16f) : Hot ? ClawTheme.Mix(ClawTheme.CaseInk, ClawTheme.Ink, .09f) : ClawTheme.CaseInk, ClawTheme.Ink);
        }
        if (!Enabled) return (ClawTheme.OnSurface(this) ? ClawTheme.Raised : ClawTheme.Surface, ClawTheme.Faint);
        if (Primary || Selected) return (Down ? ClawTheme.AccentPressed : Hot ? ClawTheme.AccentHover : ClawTheme.Accent, ClawTheme.CaseInk);
        bool raised = ClawTheme.OnSurface(this);
        return (Down ? ClawTheme.Pressed : Hot ? (raised ? ClawTheme.Pressed : ClawTheme.Raised) : raised ? ClawTheme.Raised : ClawTheme.Surface, ClawTheme.Ink);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        var (fill, ink) = Colors();
        ClawTheme.Fill(g, fill, new Rectangle(0, 0, Width, Height));
        ClawTheme.Text(g, Text, Font, ink, new Rectangle(8, 0, Width - 16, Height), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        PaintFocus(g);
    }
    protected void PaintFocus(Graphics g)
    {
        if (Focused && ShowFocusCues) ClawTheme.Frame(g, Primary || Selected ? ClawTheme.CaseInk : ClawTheme.Ink, new Rectangle(3, 3, Width - 6, Height - 6));
    }
}

/* A switch drawn in squares: a track with a block that slides to the lit end. */
internal sealed class ClawSwitch : CheckBox
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private float amount;
    private bool hover;
    internal const int TrackWidth = 46, TrackHeight = 26;
    public ClawSwitch()
    {
        AutoSize = false; Size = new(600, 44); Font = ClawTheme.Font(ClawTheme.Body); ForeColor = ClawTheme.Ink; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        animation.Tick += (_, _) => { float target = Checked ? 1 : 0; amount += (target - amount) * .32f; if (Math.Abs(target - amount) < .02f) { amount = target; animation.Stop(); } Invalidate(); };
    }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); if (ClawTheme.Motion && IsHandleCreated) animation.Start(); else amount = Checked ? 1 : 0; Invalidate(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); amount = Checked ? 1 : 0; }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        int w = LogicalToDeviceUnits(TrackWidth), h = LogicalToDeviceUnits(TrackHeight), left = Width - w - 2, top = (Height - h) / 2;
        if (Text.Length > 0) ClawTheme.Text(g, Text, Font, Enabled ? ClawTheme.Ink : ClawTheme.Faint, new Rectangle(0, 0, Math.Max(1, left - LogicalToDeviceUnits(20)), Height));
        bool on = Checked && Enabled;
        var track = on ? (hover ? ClawTheme.AccentHover : ClawTheme.Accent) : !Enabled ? ClawTheme.Surface : hover ? ClawTheme.Pressed : ClawTheme.Raised;
        ClawTheme.Fill(g, track, new Rectangle(left, top, w, h));
        int inset = LogicalToDeviceUnits(4), knob = h - 2 * inset;
        int x = left + inset + (int)Math.Round((w - 2 * inset - knob) * amount);
        ClawTheme.Fill(g, on ? ClawTheme.CaseInk : Enabled ? ClawTheme.Ink : ClawTheme.Faint, new Rectangle(x, top + inset, knob, knob));
        if (Focused && ShowFocusCues) ClawTheme.Frame(g, ClawTheme.Ink, new Rectangle(left - 4, top - 4, w + 8, h + 8));
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}

/* An orange area that lets the window handle the mouse, so the case drags, snaps and resizes like a title bar. */
internal class CaseSurface : Panel
{
    public CaseSurface() { BackColor = ClawTheme.Case; DoubleBuffered = true; }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
        base.WndProc(ref m);
    }
}

/* The window is the display in its case: an orange frame, a rail down the left for moving between
   pages, and the black screen where each page lives. The page you are on joins the screen. */
internal class ClawWindow : Form
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    internal const int RailWidth = 236, TopBar = 46, Bezel = 14;
    protected CaseSurface Rail { get; } = new() { Dock = DockStyle.Left, Width = RailWidth };
    protected CaseSurface Body { get; } = new() { Dock = DockStyle.Fill, Padding = new Padding(0, TopBar, Bezel, Bezel) };
    /* The black screen. Pages, their headers and footers go in here. */
    protected Panel ScreenArea { get; } = new() { Dock = DockStyle.Fill, BackColor = ClawTheme.Screen };
    private readonly WindowAction[] actions;
    public ClawWindow()
    {
        FormBorderStyle = FormBorderStyle.None; BackColor = ClawTheme.Case; ForeColor = ClawTheme.Ink; Font = ClawTheme.Font(); DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen; MinimumSize = new(960, 680);
        Body.Controls.Add(ScreenArea);
        actions = Enumerable.Range(0, 3).Select(i => new WindowAction(i) { Size = new(46, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right }).ToArray();
        foreach (var action in actions) { action.Click += (_, _) => Act(action.Action); Body.Controls.Add(action); }
        Body.Resize += (_, _) => PlaceActions(); PlaceActions();
        Controls.Add(Body); Controls.Add(Rail);
    }
    private void PlaceActions()
    {
        int w = LogicalToDeviceUnits(46), h = LogicalToDeviceUnits(34), right = Body.ClientSize.Width - LogicalToDeviceUnits(Bezel) + LogicalToDeviceUnits(4);
        for (int i = 0; i < actions.Length; i++) actions[i].SetBounds(right - (actions.Length - i) * w, LogicalToDeviceUnits(6), w, h);
    }
    /* Rail hidden: the case closes around the screen evenly, for full-window moments. */
    protected void ShowRail(bool visible)
    {
        Rail.Visible = visible; Body.Padding = new Padding(visible ? 0 : Bezel, TopBar, Bezel, Bezel);
    }
    private void Act(int action) { if (action == 0) WindowState = FormWindowState.Minimized; else if (action == 1) ToggleMaximize(); else Close(); }
    private void ToggleMaximize() { MaximizedBounds = Screen.FromHandle(Handle).WorkingArea; WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
        int corner = 2; DwmSetWindowAttribute(Handle, 33, ref corner, 4);
        // Windows 11 draws a thin edge around every window; give it the case's colour.
        int border = ClawTheme.Case.R | ClawTheme.Case.G << 8 | ClawTheme.Case.B << 16; DwmSetWindowAttribute(Handle, 34, ref border, 4);
    }
    protected override void OnShown(EventArgs e) { ClawTheme.Apply(this); base.OnShown(e); }
    protected override void WndProc(ref Message m)
    {
        // A double click on the case maximises or restores, as on a title bar.
        if (m.Msg == 0xA3 && m.WParam == (IntPtr)2) { ToggleMaximize(); return; }
        base.WndProc(ref m);
        // Only the case reaches here (its panels pass the mouse through): edges resize, the rest moves the window.
        if (m.Msg != 0x84) return;
        var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        int x = p.X, y = p.Y, b = LogicalToDeviceUnits(7);
        if (WindowState == FormWindowState.Normal && (x < b || y < b || x > Width - b || y > Height - b))
            m.Result = (IntPtr)(x < b ? (y < b ? 13 : y > Height - b ? 16 : 10) : x > Width - b ? (y < b ? 14 : y > Height - b ? 17 : 11) : y < b ? 12 : 15);
        else m.Result = (IntPtr)2;
    }
    /* Minimise, maximise and close: black strokes on the case; close turns black on hover. */
    private sealed class WindowAction : ClawButton
    {
        internal int Action { get; }
        internal WindowAction(int action) { Action = action; TabStop = false; }
        protected override void OnCreateControl() { base.OnCreateControl(); AccessibleName = Action == 0 ? L.Text("minimize") : Action == 1 ? L.Text("maximize_or_restore") : L.Text("close_to_tray"); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ClawTheme.Case);
            if (Hot) ClawTheme.Fill(g, Action == 2 ? ClawTheme.CaseInk : ClawTheme.CaseHover, new Rectangle(0, 0, Width, Height));
            var ink = Hot && Action == 2 ? ClawTheme.Case : ClawTheme.CaseInk;
            int x = Width / 2, y = Height / 2, s = LogicalToDeviceUnits(5);
            using var brush = new SolidBrush(ink); using var pen = new Pen(ink, LogicalToDeviceUnits(2)) { StartCap = LineCap.Square, EndCap = LineCap.Square };
            if (Action == 0) g.FillRectangle(brush, x - s, y + s - 2, 2 * s, 2);
            else if (Action == 1) ClawTheme.Frame(g, ink, new Rectangle(x - s, y - s, 2 * s, 2 * s));
            else { g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawLine(pen, x - s + 1, y - s + 1, x + s - 1, y + s - 1); g.DrawLine(pen, x + s - 1, y - s + 1, x - s + 1, y + s - 1); }
        }
    }
}
