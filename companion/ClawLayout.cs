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

/* A part of a page set straight on the screen: its name, one line on what it is for, then its controls.
   Space, not boxes, keeps one part from the next. */
internal class ClawSection : ClawStack
{
    internal Label? Heading { get; }
    internal ClawSection(string? title = null, string? description = null)
    {
        Gap = 12;
        if (title is not null) { Heading = ClawTheme.Label(title, 400, ClawTheme.Head, true, 30); Add(Heading, description is null ? 14 : 4); }
        if (description is not null) Add(ClawTheme.Label(description, 400, ClawTheme.Body, false, 22), 14);
    }
}

/* A filled block one tone up from the screen, for the few things that belong together as one object. */
internal sealed class ClawCard : ClawSection
{
    internal ClawCard(string? title = null, string? description = null) : base(title, description)
    {
        BackColor = ClawTheme.Surface; Padding = new Padding(24, 20, 24, 22);
    }
}

/* Children side by side. A child whose width is 0 shares the space left over; the row is as tall as its tallest child. */
internal sealed class ClawRow : Panel
{
    private readonly Dictionary<Control, int> fixedWidths = new();
    internal int Gap { get; set; } = 10;
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

/* An app ready to add to Home: its icon and name, with a plus that turns orange under the pointer. */
internal sealed class AppChip : ClawButton
{
    internal int AppId { get; }
    /* false: just shows the app, as setup's list of what the display will have. */
    private readonly bool adds;
    internal AppChip(DisplayApp app, bool adds = true)
    {
        AppId = app.Id; this.adds = adds; Text = app.Name; Size = new(170, 52); Font = ClawTheme.Font(ClawTheme.Body, true);
        AccessibleName = adds ? L.Text("add_app_to_home", app.Name) : app.Name; TextAlign = ContentAlignment.MiddleLeft;
        if (!adds) { Cursor = Cursors.Default; TabStop = false; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen); g.SmoothingMode = SmoothingMode.AntiAlias;
        bool hot = adds && Hot, raised = ClawTheme.OnSurface(this);
        ClawTheme.Fill(g, hot ? (raised ? ClawTheme.Pressed : ClawTheme.Raised) : raised ? ClawTheme.Raised : ClawTheme.Surface, new Rectangle(0, 0, Width, Height));
        int icon = Math.Min(LogicalToDeviceUnits(24), Height - 20), left = LogicalToDeviceUnits(12), plus = LogicalToDeviceUnits(18);
        int textLeft = left + icon + LogicalToDeviceUnits(10), textRight = Width - (adds ? plus + LogicalToDeviceUnits(18) : 10);
        AppArtwork.Draw(g, AppId, new Rectangle(left, (Height - icon) / 2, icon, icon));
        ClawTheme.Text(g, Text, Font, Enabled ? ClawTheme.Ink : ClawTheme.Faint, new Rectangle(textLeft, 0, Math.Max(1, textRight - textLeft), Height));
        if (adds)
        {
            int x = Width - LogicalToDeviceUnits(12) - plus, y = (Height - plus) / 2;
            ClawTheme.Fill(g, hot ? ClawTheme.Accent : raised ? ClawTheme.Pressed : ClawTheme.Raised, new Rectangle(x, y, plus, plus));
            using var brush = new SolidBrush(hot ? ClawTheme.CaseInk : Enabled ? ClawTheme.Ink : ClawTheme.Faint);
            int arm = plus / 4, t = Math.Max(2, LogicalToDeviceUnits(2)), cx = x + plus / 2, cy = y + plus / 2;
            g.FillRectangle(brush, cx - arm, cy - t / 2, 2 * arm, t); g.FillRectangle(brush, cx - t / 2, cy - arm, t, 2 * arm);
        }
        PaintFocus(g);
    }
}

/* Wraps equally sized children into as many columns as fit. */
internal sealed class ClawWrap : Panel
{
    internal int ItemWidth { get; set; } = 170;
    internal int ItemHeight { get; set; } = 52;
    internal int Gap { get; set; } = 8;
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

/* The page's shout: Segoe UI Black, two lines at most, drawn without GDI's side padding so it lines up with the text under it. */
internal sealed class ClawHeading : Label
{
    public ClawHeading() { AutoSize = false; UseMnemonic = false; Font = ClawTheme.Display(ClawTheme.Title); ForeColor = ClawTheme.Ink; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
    /* The height the text needs at this width, in at most two lines. */
    internal int Measure(int width)
    {
        int line = TextRenderer.MeasureText("Ag", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
        int needed = TextRenderer.MeasureText(Text.Length == 0 ? "Ag" : Text, Font, new Size(Math.Max(1, width), int.MaxValue), Flags).Height;
        return Math.Min(needed, line * 2);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor == Color.Transparent ? Parent?.BackColor ?? ClawTheme.Screen : BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
    }
}

/* A thick square bar: a moving block while the length of the work is unknown, then the share done. */
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
        Height = 12; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        timer.Tick += (_, _) => { if (!ClawTheme.Motion) { phase = .35f; timer.Stop(); } else phase = (phase + .011f) % 1.4f; Invalidate(); }; timer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        ClawTheme.Fill(g, ClawTheme.Raised, new Rectangle(0, 0, Width, Height));
        if (style == ProgressBarStyle.Marquee)
        {
            float w = Width * .28f, x = (phase - .28f) * Width; var bar = RectangleF.Intersect(new RectangleF(x, 0, w, Height), new RectangleF(0, 0, Width, Height));
            if (bar.Width > 1) ClawTheme.Fill(g, ClawTheme.Accent, bar);
        }
        else if (value > 0) ClawTheme.Fill(g, ClawTheme.Accent, new RectangleF(0, 0, Width * value / 100f, Height));
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

/* A number set large, without GDI's side padding, so it starts on the same edge as the text above it. */
internal sealed class BigNumber : Control
{
    public BigNumber() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Font = ClawTheme.Display(48); ForeColor = ClawTheme.Ink; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Point(-LogicalToDeviceUnits(2), 0), ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

/* The stages of a long job in a row, in the order they happen: done ones carry a tick, the one under
   way is the orange block, the rest wait in the dark. */
internal sealed class StageStrip : Control
{
    private string[] names = [];
    private int current;
    internal int Current => current;
    internal void Set(string[] stages, int index)
    {
        names = stages; current = index; Invalidate();
        AccessibleName = current < names.Length ? names[current] : string.Join(", ", names); AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }
    public StageStrip() { Height = 72; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); AccessibleRole = AccessibleRole.ProgressBar; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        if (names.Length == 0) return;
        int gap = LogicalToDeviceUnits(6), width = (Width - gap * (names.Length - 1)) / names.Length, pad = LogicalToDeviceUnits(16);
        for (int i = 0; i < names.Length; i++)
        {
            var block = new Rectangle(i * (width + gap), 0, i == names.Length - 1 ? Width - i * (width + gap) : width, Height);
            bool done = i < current, now = i == current;
            ClawTheme.Fill(g, now ? ClawTheme.Accent : ClawTheme.Surface, block);
            int textLeft = block.X + pad;
            if (done) { ClawTheme.Tick(g, ClawTheme.Accent, new PointF(block.X + pad + LogicalToDeviceUnits(8), block.Y + Height / 2f), LogicalToDeviceUnits(16), LogicalToDeviceUnits(3)); textLeft += LogicalToDeviceUnits(26); }
            ClawTheme.Text(g, names[i], ClawTheme.Font(ClawTheme.Body, done || now), now ? ClawTheme.CaseInk : done ? ClawTheme.Ink : ClawTheme.Faint, new Rectangle(textLeft, block.Y, block.Right - textLeft - pad / 2, Height));
        }
    }
}

/* Setup's steps down the rail: done ones carry a tick, the current one is the black tab that joins
   the screen, and the ones still to come wait in plain type. Order is their position. */
internal sealed class StepList : Control
{
    private string[] steps = [];
    private int current;
    internal int RowHeight => LogicalToDeviceUnits(48);
    internal void Set(string[] names, int index) { steps = names; current = index; Height = steps.Length * RowHeight; Invalidate(); }
    public StepList() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = ClawTheme.Case; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(ClawTheme.Case);
        int row = RowHeight, text = LogicalToDeviceUnits(46);
        var regular = ClawTheme.Font(ClawTheme.Lead); var bold = ClawTheme.Font(ClawTheme.Lead, true);
        for (int i = 0; i < steps.Length; i++)
        {
            int y = i * row;
            if (i == current) ClawTheme.Fill(g, ClawTheme.Screen, new Rectangle(0, y, Width, row));
            if (i < current) ClawTheme.Tick(g, ClawTheme.CaseInk, new PointF(LogicalToDeviceUnits(26), y + row / 2f), LogicalToDeviceUnits(16), LogicalToDeviceUnits(3));
            ClawTheme.Text(g, steps[i], i <= current ? bold : regular, i == current ? ClawTheme.Ink : ClawTheme.CaseInk, new Rectangle(text, y, Width - text - 8, row));
        }
    }
}
