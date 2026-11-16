using System.Drawing.Drawing2D;
using System.Globalization;

namespace ClawPal.Companion;

// Keep the native editor (caret, selection, IME, paste and accessibility), inside a filled square of our own.
internal sealed class ClawTextField : UserControl
{
    internal TextBox Editor { get; } = new() { BorderStyle = BorderStyle.None, BackColor = ClawTheme.Surface, ForeColor = ClawTheme.Ink };
    private bool hover;
    public ClawTextField()
    {
        AutoScaleMode = AutoScaleMode.None; Height = 44; Width = 560; MinimumSize = new(80, 40); BackColor = ClawTheme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Editor.Font = ClawTheme.Font(ClawTheme.Body); Controls.Add(Editor); TabStop = false;
        Editor.TextChanged += (_, _) => base.OnTextChanged(EventArgs.Empty);
        Editor.Enter += (_, _) => Invalidate(); Editor.Leave += (_, _) => Invalidate();
        Editor.MouseEnter += (_, _) => { hover = true; Invalidate(); }; Editor.MouseLeave += (_, _) => { hover = false; Invalidate(); };
        Resize += (_, _) => PlaceEditor(); PlaceEditor();
    }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Editor.Text; set => Editor.Text = value; }
    public string PlaceholderText { get => Editor.PlaceholderText; set => Editor.PlaceholderText = value; }
    public bool UseSystemPasswordChar { get => Editor.UseSystemPasswordChar; set => Editor.UseSystemPasswordChar = value; }
    public void Clear() => Editor.Clear();
    private void PlaceEditor() { int inset = 14 * DeviceDpi / 96; Editor.SetBounds(inset, (Height - Editor.PreferredHeight) / 2, Math.Max(1, Width - 2 * inset), Editor.PreferredHeight); }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Editor.Focus(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Editor.Focus(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var field = ClawTheme.Field(this); if (Editor.BackColor != field) { Editor.BackColor = field; BackColor = field; }
        var g = e.Graphics; g.Clear(field);
        if (ContainsFocus) ClawTheme.Frame(g, ClawTheme.Accent, ClientRectangle);
        else if (hover) ClawTheme.Fill(g, ClawTheme.Muted, new Rectangle(0, Height - 2, Width, 2));
    }
}

// A field that opens its own scrolling list: the wheel and a draggable bar move through long
// lists, typing jumps to a match, and clicking the field again closes it.
internal sealed class ChoiceBox : ClawButton
{
    public List<object> Items { get; } = new();
    private int selected = -1;
    private readonly ChoiceList list;
    private readonly ToolStripControlHost host;
    private readonly ToolStripDropDown popup;
    private long closedAt;
    private bool closedByOwnClick;
    internal ToolStripDropDown SelectionMenu => popup;
    internal ChoiceList SelectionList => list;
    public event EventHandler? SelectedIndexChanged;
    public int SelectedIndex
    {
        get => selected >= 0 && selected < Items.Count ? selected : -1;
        set { selected = value; Text = SelectedItem?.ToString() ?? L.Text("select"); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
    }
    public object? SelectedItem { get => SelectedIndex >= 0 ? Items[selected] : null; set => SelectedIndex = Items.IndexOf(value!); }
    public ChoiceBox()
    {
        Text = L.Text("select"); Size = new(300, 44); MinimumSize = new(90, 40); Font = ClawTheme.Font(ClawTheme.Body); AccessibleRole = AccessibleRole.ComboBox;
        list = new ChoiceList { Font = Font };
        host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false };
        popup = new ToolStripDropDown { Padding = Padding.Empty, AutoSize = false, DropShadowEnabled = true, Renderer = new PopupRenderer(), BackColor = ClawTheme.Raised };
        popup.Items.Add(host);
        popup.Closed += (_, e) =>
        {
            closedAt = Environment.TickCount64;
            // The press that dismissed the list lands on this field next; it must not reopen it.
            closedByOwnClick = e.CloseReason == ToolStripDropDownCloseReason.AppClicked && IsHandleCreated && ClientRectangle.Contains(PointToClient(MousePosition));
            Invalidate();
        };
        list.Chosen += index => { popup.Close(ToolStripDropDownCloseReason.ItemClicked); if (Items.Count > index) SelectedIndex = index; if (CanFocus) Focus(); };
        list.Cancelled += () => popup.Close(ToolStripDropDownCloseReason.Keyboard);
        Click += (_, _) => Toggle();
    }
    private void Toggle()
    {
        if (popup.Visible) { popup.Close(ToolStripDropDownCloseReason.ItemClicked); return; }
        if (closedByOwnClick && Environment.TickCount64 - closedAt < 500) { closedByOwnClick = false; return; }
        OpenChoices();
    }
    private void OpenChoices()
    {
        var area = Screen.FromControl(this).WorkingArea;
        int row = 42 * DeviceDpi / 96, pad = 6 * DeviceDpi / 96, width = Math.Min(Width, area.Width - 16);
        int rows = Math.Clamp(Items.Count, 1, 8), height = rows * row + 2 * pad;
        list.Font = Font; list.Reset(Items.Select(i => i.ToString() ?? "").ToArray(), SelectedIndex, row, pad, L.Text("no_choices_available"));
        list.Size = new(width, height); host.Size = list.Size; popup.Size = list.Size;
        // Opens upwards when the screen has no room below the field.
        bool above = PointToScreen(new Point(0, Height + 4 + height)).Y > area.Bottom && PointToScreen(Point.Empty).Y - height - 4 >= area.Top;
        popup.Show(this, new Point(0, above ? -height - 4 : Height + 4)); list.Focus(); list.EnsureVisible(SelectedIndex); Invalidate();
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down) { if (!popup.Visible) OpenChoices(); e.Handled = e.SuppressKeyPress = true; } else base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        bool open = popup.Visible, raised = ClawTheme.OnSurface(this);
        var fill = !Enabled ? ClawTheme.Field(this) : open || Hot ? (raised ? ClawTheme.Raised : ClawTheme.Raised) : ClawTheme.Field(this);
        ClawTheme.Fill(g, fill, new Rectangle(0, 0, Width, Height));
        ClawTheme.Text(g, Text, Font, Enabled ? ClawTheme.Ink : ClawTheme.Faint, new Rectangle(LogicalToDeviceUnits(14), 0, Math.Max(1, Width - LogicalToDeviceUnits(52)), Height));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Enabled ? (open ? ClawTheme.Accent : ClawTheme.Muted) : ClawTheme.Faint, LogicalToDeviceUnits(2)) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter };
        int x = Width - LogicalToDeviceUnits(24), y = Height / 2, d = open ? -1 : 1, a = LogicalToDeviceUnits(5);
        g.DrawLines(pen, new Point[] { new(x - a, y - a / 2 * d), new(x, y + a / 2 * d), new(x + a, y - a / 2 * d) });
        if (Enabled && (open || (Focused && ShowFocusCues))) ClawTheme.Frame(g, ClawTheme.Accent, ClientRectangle);
    }
    protected override void Dispose(bool disposing) { if (disposing) { popup.Dispose(); list.Dispose(); } base.Dispose(disposing); }
    private sealed class PopupRenderer : ToolStripProfessionalRenderer
    {
        internal PopupRenderer() : base(new ClawMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(ClawTheme.Raised); }
    }
}

internal sealed class ChoiceList : Control
{
    private string[] items = [];
    private string empty = "";
    private int selected = -1, hover = -1, row = 42, pad = 6, dragFrom = -1;
    private float offset, dragOffset;
    internal event Action<int>? Chosen;
    internal event Action? Cancelled;
    internal int Count => items.Length;
    internal int Hover => hover;
    internal int ScrollOffset => (int)offset;
    internal int MaximumOffset => Math.Max(0, items.Length * row - (Height - 2 * pad));
    internal string Placeholder => items.Length == 0 ? empty : "";
    internal ChoiceList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
        TabStop = true; BackColor = ClawTheme.Raised; AccessibleRole = AccessibleRole.List;
    }
    internal void Reset(string[] values, int chosen, int rowHeight, int padding, string placeholder)
    {
        items = values; selected = chosen; hover = chosen; row = rowHeight; pad = padding; empty = placeholder; offset = 0; dragFrom = -1; BackColor = ClawTheme.Raised; Invalidate();
    }
    private bool Scrolls => MaximumOffset > 0;
    private Rectangle Track => new(Width - pad - 6, pad + 2, 6, Height - 2 * pad - 4);
    private Rectangle Thumb
    {
        get
        {
            var t = Track; int view = Height - 2 * pad, content = Math.Max(1, items.Length * row); int h = Math.Max(24, t.Height * view / content);
            int y = t.Top + (int)((t.Height - h) * (MaximumOffset == 0 ? 0 : offset / MaximumOffset)); return new(t.Left, y, t.Width, h);
        }
    }
    internal Rectangle RowBounds(int index) => new(pad, pad + index * row - (int)offset, Width - 2 * pad - (Scrolls ? 14 : 0), row);
    private int IndexAt(Point p) { if (p.Y < pad || p.Y >= Height - pad || p.X >= Width - pad - (Scrolls ? 14 : 0)) return -1; int i = (int)((p.Y - pad + offset) / row); return i >= 0 && i < items.Length ? i : -1; }
    private void ScrollTo(float value) { offset = Math.Clamp(value, 0, MaximumOffset); Invalidate(); }
    internal void EnsureVisible(int index)
    {
        if (index < 0 || index >= items.Length) return; int top = index * row, view = Height - 2 * pad;
        if (top < offset) ScrollTo(top); else if (top + row > offset + view) ScrollTo(top + row - view);
    }
    private void HoverAt(int index) { if (items.Length == 0) return; hover = Math.Clamp(index, 0, items.Length - 1); EnsureVisible(hover); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int lines = SystemInformation.MouseWheelScrollLines; if (lines <= 0) lines = 3;
        ScrollTo(offset - e.Delta / 120f * row * Math.Min(lines, 4)); hover = IndexAt(e.Location);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left || !Scrolls || e.X < Width - pad - 14) return;
        var thumb = Thumb;
        // Pressing the track jumps a page; the thumb itself follows the pointer.
        if (e.Y < thumb.Top) ScrollTo(offset - (Height - 2 * pad)); else if (e.Y > thumb.Bottom) ScrollTo(offset + (Height - 2 * pad));
        else { dragFrom = e.Y; dragOffset = offset; Capture = true; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragFrom >= 0) { var t = Track; float travel = Math.Max(1, t.Height - Thumb.Height); ScrollTo(dragOffset + (e.Y - dragFrom) * MaximumOffset / travel); return; }
        int next = IndexAt(e.Location); if (next != hover) { hover = next; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragFrom >= 0) { dragFrom = -1; Capture = false; return; }
        if (e.Button == MouseButtons.Left && IndexAt(e.Location) is var index and >= 0) Chosen?.Invoke(index);
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (dragFrom < 0) { hover = -1; Invalidate(); } }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Enter or Keys.Escape || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max(1, (Height - 2 * pad) / row - 1), from = hover >= 0 ? hover : selected;
        switch (e.KeyCode)
        {
            case Keys.Down: HoverAt(from + 1); break; case Keys.Up: HoverAt(from < 0 ? 0 : from - 1); break;
            case Keys.PageDown: HoverAt(from + page); break; case Keys.PageUp: HoverAt(from - page); break;
            case Keys.Home: HoverAt(0); break; case Keys.End: HoverAt(items.Length - 1); break;
            case Keys.Enter: if (hover >= 0) Chosen?.Invoke(hover); break; case Keys.Escape: Cancelled?.Invoke(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }
    /* Typing a letter jumps to the next entry that starts with it. */
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e); if (char.IsControl(e.KeyChar) || items.Length == 0) return;
        int start = hover >= 0 ? hover : selected;
        for (int step = 1; step <= items.Length; step++)
        {
            int i = ((start < 0 ? -1 : start) + step) % items.Length;
            if (items[i].StartsWith(e.KeyChar.ToString(), StringComparison.CurrentCultureIgnoreCase)) { HoverAt(i); e.Handled = true; return; }
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(ClawTheme.Raised);
        var view = new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad); g.SetClip(view);
        if (items.Length == 0) ClawTheme.Text(g, empty, Font, ClawTheme.Muted, new Rectangle(pad + 12, pad, Width - 2 * pad - 24, row));
        int first = Math.Max(0, (int)(offset / row)), last = Math.Min(items.Length - 1, (int)((offset + view.Height) / row));
        for (int i = first; i <= last; i++)
        {
            var r = RowBounds(i); bool chosen = i == selected;
            if (i == hover) ClawTheme.Fill(g, ClawTheme.Pressed, r);
            ClawTheme.Text(g, items[i], Font, chosen ? ClawTheme.Accent : ClawTheme.Ink, new Rectangle(r.X + 12, r.Y, r.Width - (chosen ? 44 : 24), r.Height));
            if (chosen) ClawTheme.Tick(g, ClawTheme.Accent, new PointF(r.Right - 20, r.Y + r.Height / 2f), 14, 2);
        }
        g.ResetClip();
        if (Scrolls) { ClawTheme.Fill(g, ClawTheme.Surface, Track); ClawTheme.Fill(g, dragFrom >= 0 ? ClawTheme.Accent : ClawTheme.Muted, Thumb); }
    }
}

internal enum ClawNumberFormat { Hour, Duration }

// Editable time with small step buttons, instead of forcing users through a list of numbers.
internal sealed class ClawTimeInput : UserControl
{
    private readonly TextBox editor = new() { BorderStyle = BorderStyle.None, BackColor = ClawTheme.Surface, ForeColor = ClawTheme.Ink, TextAlign = HorizontalAlignment.Center };
    private readonly ClawButton minus = new() { Text = "−", Width = 44 }, plus = new() { Text = "+", Width = 44 };
    private int value;
    internal int Minimum { get; set; }
    internal int Maximum { get; set; } = 23;
    internal int Step { get; set; } = 1;
    internal ClawNumberFormat Format { get; set; }
    internal event EventHandler? ValueChanged;
    internal int Value { get => value; set { int next = Math.Clamp(value, Minimum, Maximum); bool changed = this.value != next; this.value = next; FormatText(); if (changed) ValueChanged?.Invoke(this, EventArgs.Empty); } }
    internal TextBox Editor => editor;
    internal ClawTimeInput()
    {
        AutoScaleMode = AutoScaleMode.None; Size = new(220, 52); MinimumSize = new(150, 44); TabStop = false; DoubleBuffered = true; BackColor = ClawTheme.Surface;
        editor.Font = ClawTheme.Font(ClawTheme.Head, true); editor.AccessibleName = L.Text("time_type_a_value_or_use_the_decrease_and");
        minus.Font = plus.Font = ClawTheme.Font(ClawTheme.Head, true);
        minus.AccessibleName = L.Text("decrease_time"); plus.AccessibleName = L.Text("increase_time");
        minus.Click += (_, _) => Value -= Step; plus.Click += (_, _) => Value += Step;
        Controls.AddRange([minus, editor, plus]); editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Value += Step; e.SuppressKeyPress = true; } else if (e.KeyCode == Keys.Down) { Value -= Step; e.SuppressKeyPress = true; }
            else if (e.KeyCode is Keys.Enter or Keys.Escape) { if (e.KeyCode == Keys.Enter) Commit(); else FormatText(); e.SuppressKeyPress = true; }
        };
        editor.TextChanged += (_, _) => { if (TryParse(editor.Text, out int next) && next != value) { value = next; minus.Enabled = Enabled && value > Minimum; plus.Enabled = Enabled && value < Maximum; ValueChanged?.Invoke(this, EventArgs.Empty); } Invalidate(); };
        editor.Leave += (_, _) => Commit(); editor.Enter += (_, _) => { editor.SelectAll(); Invalidate(); };
        Resize += (_, _) => LayoutInput(); LayoutInput(); FormatText();
    }
    private bool TryParse(string text, out int result)
    {
        result = 0; var parts = text.Trim().Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first)) return false;
        int second = 0; if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out second) || second is < 0 or > 59)) return false;
        if (Format == ClawNumberFormat.Hour) { if (second != 0) return false; result = first; }
        else { if (first > 60) return false; result = first * 60 + second; }
        return result >= Minimum && result <= Maximum;
    }
    private void Commit() { if (TryParse(editor.Text, out int next)) Value = next; else FormatText(); Invalidate(); }
    private void FormatText() { string text = Format == ClawNumberFormat.Hour ? $"{value:00}:00" : $"{value / 60}:{value % 60:00}"; if (editor.Text != text) editor.Text = text; minus.Enabled = Enabled && value > Minimum; plus.Enabled = Enabled && value < Maximum; Invalidate(); }
    private void LayoutInput() { int step = 44 * DeviceDpi / 96, inset = 4 * DeviceDpi / 96; minus.SetBounds(inset, inset, step, Height - 2 * inset); plus.SetBounds(Width - step - inset, inset, step, Height - 2 * inset); editor.SetBounds(step + 12, (Height - editor.PreferredHeight) / 2, Math.Max(1, Width - 2 * step - 24), editor.PreferredHeight); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); editor.ForeColor = Enabled ? ClawTheme.Ink : ClawTheme.Faint; FormatText(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var field = ClawTheme.Field(this); if (editor.BackColor != field) editor.BackColor = field;
        e.Graphics.Clear(field);
        if (ContainsFocus) ClawTheme.Frame(e.Graphics, ClawTheme.Accent, ClientRectangle);
    }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); BackColor = ClawTheme.Field(this); }
}

/* A thick track with a square handle; the value reads large beside it. */
internal sealed class ClawSlider : Control
{
    private int value = 40;
    private bool dragging;
    internal int Minimum { get; set; } = 5;
    internal int Maximum { get; set; } = 90;
    internal event EventHandler? ValueChanged;
    internal int Value { get => value; set { int next = Math.Clamp(value, Minimum, Maximum); if (this.value == next) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); } }
    internal ClawSlider() { Size = new(320, 52); TabStop = true; DoubleBuffered = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider; AccessibleName = L.Text("dimmed_brightness_percent"); Font = ClawTheme.Display(ClawTheme.Head + 1); }
    private Rectangle Rail => new(LogicalToDeviceUnits(12), Height / 2 - LogicalToDeviceUnits(3), Math.Max(1, Width - LogicalToDeviceUnits(104)), LogicalToDeviceUnits(6));
    private void PointValue(int x) => Value = Minimum + (int)Math.Round(Math.Clamp((x - Rail.Left) / (double)Rail.Width, 0, 1) * (Maximum - Minimum));
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); Capture = dragging = true; PointValue(e.X); Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) PointValue(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { Capture = dragging = false; Invalidate(); base.OnMouseUp(e); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? 10 : 1;
        switch (e.KeyCode) { case Keys.Right: case Keys.Up: Value += step; break; case Keys.Left: case Keys.Down: Value -= step; break; case Keys.Home: Value = Minimum; break; case Keys.End: Value = Maximum; break; case Keys.PageUp: Value += 10; break; case Keys.PageDown: Value -= 10; break; default: base.OnKeyDown(e); return; }
        e.Handled = e.SuppressKeyPress = true;
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        var rail = Rail; int x = rail.Left + (int)(rail.Width * (value - Minimum) / (float)Math.Max(1, Maximum - Minimum));
        ClawTheme.Fill(g, ClawTheme.OnSurface(this) ? ClawTheme.Pressed : ClawTheme.Raised, rail);
        ClawTheme.Fill(g, Enabled ? ClawTheme.Accent : ClawTheme.Faint, new Rectangle(rail.Left, rail.Top, x - rail.Left, rail.Height));
        int kw = LogicalToDeviceUnits(14), kh = LogicalToDeviceUnits(30);
        var knob = new Rectangle(x - kw / 2, Height / 2 - kh / 2, kw, kh);
        ClawTheme.Fill(g, !Enabled ? ClawTheme.Faint : dragging ? ClawTheme.Accent : ClawTheme.Ink, knob);
        if (Focused && ShowFocusCues) ClawTheme.Frame(g, ClawTheme.Ink, Rectangle.Inflate(knob, 5, 5));
        ClawTheme.Text(g, $"{Value}%", Font, Enabled ? ClawTheme.Ink : ClawTheme.Faint, new Rectangle(Width - LogicalToDeviceUnits(80), 0, LogicalToDeviceUnits(80), Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibility(this);
    private sealed class SliderAccessibility(ClawSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => $"{owner.Value}%"; set { if (int.TryParse(value?.TrimEnd('%'), out int number)) owner.Value = number; } }
        public override string? DefaultAction => L.Text("increase_brightness");
        public override void DoDefaultAction() { if (owner.Enabled) owner.Value++; }
    }
}
