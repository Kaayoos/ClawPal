using System.Drawing.Drawing2D;

namespace ClawPal.Companion;

/* Lays its children out top to bottom at full width, measuring wrapped text, and sizes itself
   to fit. Pages are built from these instead of fixed coordinates, so nothing clips or overlaps
   when the window, language or text scale changes. */
internal class ClawStack : Panel
{
    internal int Gap { get; set; } = 12;
    private bool arranging;
    public ClawStack() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); }
    private readonly Dictionary<Control, int> spacing = new();
    /* after: the space below this control, instead of Gap. */
    internal T Add<T>(T control, int after = -1) where T : Control { if (after >= 0) spacing[control] = after; Controls.Add(control); return control; }
    internal static int TextHeight(Label label, int width)
    {
        if (string.IsNullOrEmpty(label.Text)) return 0;
        var size = TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        return size.Height + 4;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arranging) return; arranging = true;
        try
        {
            int width = Math.Max(1, ClientSize.Width - Padding.Horizontal), y = Padding.Top, last = 0;
            foreach (Control child in Controls)
            {
                if (!child.Visible) continue;
                int height = child is Label label && !label.AutoSize && !label.AutoEllipsis ? TextHeight(label, width) : child.Height;
                child.SetBounds(Padding.Left, y, width, height);
                if (child is ClawStack or ClawRow or ClawWrap) { child.PerformLayout(); height = child.Height; }
                last = spacing.TryGetValue(child, out int after) ? after : Gap;
                y += height + last;
            }
            y -= last;
            int total = y + Padding.Bottom;
            if (Height != total) Height = total;
        }
        finally { arranging = false; }
    }
    protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); e.Control!.Resize += ChildResized; e.Control.VisibleChanged += ChildResized; e.Control.TextChanged += ChildResized; }
    protected override void OnControlRemoved(ControlEventArgs e) { base.OnControlRemoved(e); spacing.Remove(e.Control!); e.Control!.Resize -= ChildResized; e.Control.VisibleChanged -= ChildResized; e.Control.TextChanged -= ChildResized; }
    private void ChildResized(object? sender, EventArgs e) { if (!arranging) PerformLayout(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (!arranging) PerformLayout(); Parent?.PerformLayout(); }
}

/* A rounded section with a title. Everything inside sits on the surface colour. */
internal sealed class ClawCard : ClawStack
{
    internal Label? Heading { get; }
    internal ClawCard(string? title = null, string? description = null)
    {
        BackColor = ClawTheme.Surface; Padding = new Padding(22, 18, 22, 20); Gap = 12;
        if (title is not null) { Heading = ClawTheme.Label(title, 400, 12, true, 30); Add(Heading, description is null ? -1 : 2); }
        if (description is not null) Add(ClawTheme.Label(description, 400, 9, false, 20));
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? ClawTheme.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        ClawTheme.Fill(e.Graphics, ClawTheme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), 12);
        using var outline = ClawTheme.Round(new RectangleF(.5f, .5f, Width - 2, Height - 2), 12); using var pen = new Pen(ClawTheme.Mix(ClawTheme.Surface, ClawTheme.Border, .7f)); e.Graphics.DrawPath(pen, outline);
    }
}

/* Children side by side. A child whose width is 0 shares the space left over; the row is as tall as its tallest child. */
internal sealed class ClawRow : Panel
{
    private readonly Dictionary<Control, int> fixedWidths = new();
    internal int Gap { get; set; } = 12;
    /* Below this width the children stack instead, each at full width. */
    internal int StackBelow { get; set; }
    private bool arranging;
    internal T Add<T>(T control, int width = 0) where T : Control { fixedWidths[control] = width; Controls.Add(control); return control; }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arranging) return; arranging = true;
        try
        {
            var items = Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            int total = ClientSize.Width, height = 0;
            if (StackBelow > 0 && total < StackBelow)
            {
                int y = 0;
                foreach (var c in items) { int h = Measure(c, total); c.SetBounds(0, y, total, h); y += h + Gap; }
                height = Math.Max(0, y - Gap);
            }
            else
            {
                int fixedSum = items.Sum(c => fixedWidths.GetValueOrDefault(c)), flexible = items.Count(c => fixedWidths.GetValueOrDefault(c) == 0);
                int share = flexible == 0 ? 0 : Math.Max(40, (total - fixedSum - Gap * (items.Length - 1)) / flexible), x = 0;
                foreach (var c in items) { int w = fixedWidths.GetValueOrDefault(c) is > 0 and var f ? f : share; int h = Measure(c, w); c.SetBounds(x, 0, w, h); x += w + Gap; height = Math.Max(height, h); }
            }
            if (Height != height) Height = height;
        }
        finally { arranging = false; }
    }
    private static int Measure(Control c, int width)
    {
        if (c is Label label && !label.AutoSize && !label.AutoEllipsis) return ClawStack.TextHeight(label, width);
        if (c is ClawStack stack) { stack.Width = width; stack.PerformLayout(); return stack.Height; }
        return c.Height;
    }
    protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); e.Control!.TextChanged += Relayout; e.Control.VisibleChanged += Relayout; }
    private void Relayout(object? sender, EventArgs e) { if (!arranging) { PerformLayout(); Parent?.PerformLayout(); } }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (!arranging) PerformLayout(); }
}

/* An app ready to add to Home: its icon and name, with a plus. */
internal sealed class AppChip : ClawButton
{
    internal int AppId { get; }
    /* false: just shows the app, as setup's list of what the display will have. */
    private readonly bool adds;
    internal AppChip(DisplayApp app, bool adds = true) { AppId = app.Id; this.adds = adds; Text = app.Name; Size = new(170, 52); Font = ClawTheme.Font(9.5f, true); AccessibleName = adds ? L.Text("add_app_to_home", app.Name) : app.Name; TextAlign = ContentAlignment.MiddleLeft; if (!adds) { Cursor = Cursors.Default; TabStop = false; } }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        bool hot = adds && ClientRectangle.Contains(PointToClient(MousePosition)) && Enabled;
        ClawTheme.Fill(g, hot ? ClawTheme.Hover : ClawTheme.OnSurface(this) ? ClawTheme.Hover : ClawTheme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), 10);
        using (var outline = ClawTheme.Round(new RectangleF(.5f, .5f, Width - 2, Height - 2), 10)) using (var pen = new Pen(hot ? ClawTheme.Accent : ClawTheme.Border)) g.DrawPath(pen, outline);
        int icon = Math.Min(22, Height - 24), left = 12, plusSize = 18, textLeft = left + icon + 10, textRight = Width - (adds ? plusSize + 16 : 8);
        AppArtwork.Draw(g, AppId, new Rectangle(left, (Height - icon) / 2, icon, icon));
        ClawTheme.Text(g, Text, Font, Enabled ? ClawTheme.Ink : ClawTheme.Muted, new Rectangle(textLeft, 0, Math.Max(1, textRight - textLeft), Height));
        if (!adds) return;
        float cx = Width - 10 - plusSize / 2f, cy = Height / 2f;
        ClawTheme.Fill(g, hot ? ClawTheme.Accent : ClawTheme.Mix(ClawTheme.Hover, ClawTheme.Ink, .08f), new RectangleF(cx - plusSize / 2f, cy - plusSize / 2f, plusSize, plusSize), plusSize / 2f);
        using var plus = new Pen(hot ? ClawTheme.Background : Enabled ? ClawTheme.Ink : ClawTheme.Muted, 1.6f);
        g.DrawLine(plus, cx - 4, cy, cx + 4, cy); g.DrawLine(plus, cx, cy - 4, cx, cy + 4);
    }
}

/* Wraps equally sized children into as many columns as fit. */
internal sealed class ClawWrap : Panel
{
    internal int ItemWidth { get; set; } = 170;
    internal int ItemHeight { get; set; } = 52;
    internal int Gap { get; set; } = 10;
    private bool arranging;
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (arranging) return; arranging = true;
        try
        {
            var items = Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            int columns = Math.Max(1, (ClientSize.Width + Gap) / (ItemWidth + Gap)), width = (ClientSize.Width - Gap * (columns - 1)) / columns;
            for (int i = 0; i < items.Length; i++) items[i].SetBounds(i % columns * (width + Gap), i / columns * (ItemHeight + Gap), width, ItemHeight);
            int rows = (items.Length + columns - 1) / columns, height = rows == 0 ? 0 : rows * ItemHeight + (rows - 1) * Gap;
            if (Height != height) { Height = height; Parent?.PerformLayout(); }
        }
        finally { arranging = false; }
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (!arranging) PerformLayout(); }
}

/* Large text drawn without GDI's side padding, so a page title lines up with the text under it. */
internal sealed class ClawHeading : Label
{
    public ClawHeading() { AutoSize = false; UseMnemonic = false; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor == Color.Transparent ? Parent?.BackColor ?? ClawTheme.Background : BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/* A thin rounded bar: a moving segment while the length of the work is unknown, then the share done. */
internal sealed class ClawProgress : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private ProgressBarStyle style = ProgressBarStyle.Marquee;
    private int value;
    private float phase;
    internal ProgressBarStyle Style { get => style; set { style = value; if (value == ProgressBarStyle.Marquee) timer.Start(); else timer.Stop(); Invalidate(); } }
    internal int Value { get => value; set { this.value = Math.Clamp(value, 0, 100); Invalidate(); } }
    public ClawProgress()
    {
        Height = 8; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        timer.Tick += (_, _) => { phase = (phase + .012f) % 1.4f; Invalidate(); }; timer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Background);
        float r = Height / 2f; ClawTheme.Fill(g, ClawTheme.Track, new RectangleF(0, 0, Width - 1, Height - 1), r);
        if (style == ProgressBarStyle.Marquee)
        {
            float w = Width * .3f, x = (phase - .3f) * Width; var bar = RectangleF.Intersect(new RectangleF(x, 0, w, Height - 1), new RectangleF(0, 0, Width - 1, Height - 1));
            if (bar.Width > 1) ClawTheme.Fill(g, ClawTheme.Accent, bar, Math.Min(r, bar.Width / 2));
        }
        else if (value > 0) ClawTheme.Fill(g, ClawTheme.Accent, new RectangleF(0, 0, Math.Max(Height, (Width - 1) * value / 100f), Height - 1), r);
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

/* Setup's steps: done ones carry a tick, the current one is lit, later ones wait in grey. */
internal sealed class StepList : Control
{
    private string[] steps = [];
    private int current;
    internal int RowHeight => LogicalToDeviceUnits(46);
    internal void Set(string[] names, int index) { steps = names; current = index; Height = steps.Length * RowHeight; Invalidate(); }
    public StepList() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Surface);
        int row = RowHeight, dot = LogicalToDeviceUnits(26);
        using var number = ClawTheme.Font(9, true); using var regular = ClawTheme.Font(10.5f); using var bold = ClawTheme.Font(10.5f, true);
        for (int i = 0; i < steps.Length; i++)
        {
            int y = i * row, cy = y + row / 2; var circle = new RectangleF(1, cy - dot / 2f, dot, dot);
            if (i < steps.Length - 1) { using var line = new Pen(i < current ? ClawTheme.Accent : ClawTheme.Border, 2); g.DrawLine(line, 1 + dot / 2f, cy + dot / 2f + 3, 1 + dot / 2f, cy + row - dot / 2f - 3); }
            if (i < current)
            {
                using var fill = new SolidBrush(ClawTheme.Accent); g.FillEllipse(fill, circle);
                using var tick = new Pen(ClawTheme.Background, 2f); float x = circle.X + dot / 2f; g.DrawLines(tick, new PointF[] { new(x - 5, cy), new(x - 1.5f, cy + 3.5f), new(x + 5, cy - 4) });
            }
            else
            {
                using var ring = new Pen(i == current ? ClawTheme.Accent : ClawTheme.Border, 2); g.DrawEllipse(ring, circle);
                ClawTheme.Text(g, (i + 1).ToString(), number, i == current ? ClawTheme.Accent : ClawTheme.Muted, Rectangle.Round(circle), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            ClawTheme.Text(g, steps[i], i == current ? bold : regular, i == current ? ClawTheme.Ink : i < current ? ClawTheme.Ink : ClawTheme.Muted, new Rectangle(dot + LogicalToDeviceUnits(14), y, Width - dot - LogicalToDeviceUnits(14), row));
        }
    }
}
