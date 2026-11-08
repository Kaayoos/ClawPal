using System.Drawing.Drawing2D;

namespace ClawPal.Companion;

/* The deck as the display lays it out: the same columns, the same tiles, in the current look.
 * Click a tile to edit it; drag it onto another slot to move or swap it. Arrows move the
 * selection, Alt+arrows move the tile and Delete removes it. */
internal sealed class DeckPreview : Control
{
    private ShortcutDeck deck;
    private readonly BoardDefinition board;
    private readonly Func<string> language;
    private int selected, dragFrom = -1, dropAt = -1;
    private Point pressAt;
    internal event Action<int>? SelectedChanged;
    internal event Action<ShortcutDeck>? Changed;
    internal int Selected => selected;
    internal ShortcutDeck Deck => deck;

    internal DeckPreview(ShortcutDeck value, string boardId, Func<string> displayLanguage)
    {
        deck = value; board = BoardCatalog.Get(boardId); language = displayLanguage;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true; AccessibleRole = AccessibleRole.List; AccessibleName = L.Text("shortcuts_on_your_display");
        Resize += (_, _) => Invalidate();
    }
    internal void SetDeck(ShortcutDeck value) { deck = value; selected = Math.Min(selected, deck.Size - 1); Invalidate(); }
    internal void Select(int slot) { selected = Math.Clamp(slot, 0, deck.Size - 1); Invalidate(); SelectedChanged?.Invoke(selected); }

    private bool Tiny => board.Height < 400;
    private int Columns => deck.Size == 6 || (Tiny && deck.Size == 12) ? 3 : 4;
    private int Rows => (deck.Size + Columns - 1) / Columns;
    // The screen at the board's shape; the smallest boards scroll by views, so their views stack here.
    internal Rectangle Screen
    {
        get
        {
            float aspect = board.Width / (float)(Tiny ? (board.Height - 44) * ((Rows + 1) / 2) + 44 : board.Height);
            int width = (int)Math.Min(Width - 2, (Height - 2) * aspect), height = (int)(width / aspect);
            return new((Width - width) / 2, (Height - height) / 2, width, height);
        }
    }
    private float Ratio => Screen.Width / (float)board.Width;
    internal RectangleF TileBounds(int slot)
    {
        var screen = Screen; float s = Ratio, body = board.Height - 44;
        int column = slot % Columns, row = slot / Columns;
        float x0, y0, w, h, gap;
        if (board.Width >= 800) { x0 = 28; y0 = 100; w = 744; h = 324; gap = 14; }
        else if (!Tiny) { x0 = 12; y0 = 50; w = board.Width - 24; h = body - 58; gap = 10; }
        else { x0 = 8; y0 = 28; w = board.Width - 16; h = body - 32; gap = 6; }
        int rowsPerView = Tiny ? 2 : Rows;
        float tw = (w - gap * (Columns - 1)) / Columns, th = (h - gap * (rowsPerView - 1)) / rowsPerView;
        float y = (Tiny ? row / 2 * body : 0) + y0 + row % rowsPerView * (th + gap);
        return new(screen.X + (x0 + column * (tw + gap)) * s, screen.Y + y * s, tw * s, th * s);
    }
    private int SlotAt(Point point)
    {
        for (int slot = 0; slot < deck.Size; ++slot) if (TileBounds(slot).Contains(point)) return slot;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Background);
        var screen = Screen; float s = Ratio;
        ClawTheme.Fill(g, ClawTheme.Background, screen, 12);
        using (var frame = ClawTheme.Round(new RectangleF(screen.X + .5f, screen.Y + .5f, screen.Width - 1, screen.Height - 1), 12))
        using (var pen = new Pen(ClawTheme.Border)) g.DrawPath(pen, frame);
        using var heading = new Font("Segoe UI", Math.Max(8, (board.Width >= 800 ? 24 : Tiny ? 14 : 20) * s), GraphicsUnit.Pixel);
        float pad = (board.Width >= 800 ? 28 : 12) * s;
        int views = Tiny ? (Rows + 1) / 2 : 1;
        for (int view = 0; view < views; ++view)
        {
            float top = screen.Y + view * (board.Height - 44) * s + (board.Width >= 800 ? 22 : Tiny ? 4 : 8) * s;
            ClawTheme.Text(g, L.DisplayText(language(), "shortcuts"), heading, ClawTheme.Ink, Rectangle.Round(new RectangleF(screen.X + pad, top, screen.Width / 2f, heading.Height + 6)));
        }
        if (board.Width >= 800) using (var rule = new Pen(ClawTheme.Border)) g.DrawLine(rule, screen.X + pad, screen.Y + 84 * s, screen.Right - pad, screen.Y + 84 * s);
        for (int slot = 0; slot < deck.Size; ++slot) if (slot != dragFrom || dropAt < 0) PaintTile(g, slot, TileBounds(slot), deck.At(slot));
        if (dragFrom >= 0 && dropAt >= 0)
        {
            var target = TileBounds(dropAt);
            PaintTile(g, dragFrom, target, deck.At(dragFrom));
            using var path = ClawTheme.Round(target, 10 * s); using var pen = new Pen(ClawTheme.Accent, 2); g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = ClawTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 8); using var pen = new Pen(ClawTheme.Accent) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focus);
        }
    }

    private void PaintTile(Graphics g, int slot, RectangleF r, ShortcutTile? tile)
    {
        float s = Ratio, radius = (board.Width >= 800 ? 14 : Tiny ? 10 : 12) * s;
        bool outlined = !ClawTheme.Mascot;
        using var shape = ClawTheme.Round(r, radius);
        if (tile is null)
        {
            using var dash = new Pen(Color.FromArgb(outlined ? 110 : 170, ClawTheme.Border)) { DashStyle = DashStyle.Dash };
            g.DrawPath(dash, shape);
            if (slot == selected) using (var plus = new Pen(ClawTheme.Muted, 2)) { var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2); float a = Math.Min(r.Width, r.Height) * .12f; g.DrawLine(plus, c.X - a, c.Y, c.X + a, c.Y); g.DrawLine(plus, c.X, c.Y - a, c.X, c.Y + a); }
        }
        else
        {
            if (outlined) { using var pen = new Pen(ClawTheme.Border); g.DrawPath(pen, shape); }
            else ClawTheme.Fill(g, ClawTheme.Surface, r, radius);
            var ink = ShortcutArt.ColorOf(tile.Color);
            bool named = tile.Label.Length > 0, side = r.Width / s >= 150 && r.Height / s < 96;
            using var font = new Font("Segoe UI", Math.Max(7, ((r.Width / s) < 120 || Tiny ? 14 : 16) * s * .92f), GraphicsUnit.Pixel);
            RectangleF icon, text;
            if (!named) { float size = Math.Min(r.Width, r.Height) * .5f; icon = new(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size); text = RectangleF.Empty; }
            else if (side) { float size = Math.Min(40 * s, r.Height - 30 * s); icon = new(r.X + 14 * s, r.Y + (r.Height - size) / 2, size, size); text = new(icon.Right + 12 * s, r.Y, r.Right - icon.Right - 20 * s, r.Height); }
            else { float size = Math.Clamp(r.Height - font.Height - 28 * s, 16 * s, 48 * s), top = r.Y + (r.Height - size - 6 * s - font.Height) / 2; icon = new(r.X + (r.Width - size) / 2, top, size, size); text = new(r.X + 4, top + size + 6 * s, r.Width - 8, font.Height + 2); }
            if (tile.UsesAppIcon && AppIcons.Load(tile.Target) is { } image) lock (image) g.DrawImage(image, icon);
            else ShortcutArt.Draw(g, tile.Icon, icon, ink);
            if (named) ClawTheme.Text(g, tile.Label, font, ClawTheme.Ink, Rectangle.Round(text),
                (side ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        if (slot == selected) { using var pen = new Pen(ClawTheme.Accent, 2); using var outline = ClawTheme.Round(RectangleF.Inflate(r, 1, 1), radius + 1); g.DrawPath(pen, outline); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        int slot = SlotAt(e.Location); if (slot < 0) return;
        Select(slot); pressAt = e.Location; dragFrom = deck.At(slot) is null ? -1 : slot; dropAt = -1; Capture = dragFrom >= 0;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = SlotAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
        // The press itself starts a drag; WinForms reads the live mouse buttons here, not the message.
        if (dragFrom < 0 || !Capture) return;
        if (dropAt < 0 && Math.Abs(e.X - pressAt.X) + Math.Abs(e.Y - pressAt.Y) < 6) return;
        int over = SlotAt(e.Location);
        if (over != dropAt && over >= 0) { dropAt = over; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); Capture = false;
        if (dragFrom >= 0 && dropAt >= 0 && dropAt != dragFrom) { deck = deck.Move(dragFrom, dropAt); Changed?.Invoke(deck); Select(dropAt); }
        dragFrom = dropAt = -1; Invalidate();
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -Columns, Keys.Down => Columns, _ => 0 };
        if (step != 0)
        {
            int to = selected + step;
            if (to >= 0 && to < deck.Size)
            {
                if (e.Alt && deck.At(selected) is not null) { deck = deck.Move(selected, to); Changed?.Invoke(deck); }
                Select(to);
            }
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete && deck.At(selected) is not null) { deck = deck.Set(selected, null); Changed?.Invoke(deck); Invalidate(); e.Handled = true; }
        else base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/* A small square choice: one of the shared icons in the tile's colour, or a colour dot. */
internal sealed class ArtChoice : ClawButton
{
    internal string Value { get; }
    private readonly bool colour;
    internal Color Ink { get; set; } = ClawTheme.Accent;
    internal Bitmap? Picture { get; set; }
    internal ArtChoice(string value, bool isColour, string name)
    {
        Value = value; colour = isColour; Text = ""; Size = new(40, 40); AccessibleName = name;
        var tip = new ToolTip(); tip.SetToolTip(this, name); Disposed += (_, _) => tip.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Background);
        bool hover = ClientRectangle.Contains(PointToClient(MousePosition));
        ClawTheme.Fill(g, hover && !Selected ? ClawTheme.Hover : ClawTheme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), 8);
        var inner = new RectangleF(9, 9, Width - 18, Height - 18);
        if (colour) using (var fill = new SolidBrush(ShortcutArt.ColorOf(Value))) g.FillEllipse(fill, inner);
        else if (Picture is { } image) lock (image) g.DrawImage(image, inner);
        else ShortcutArt.Draw(g, Value, inner, Ink);
        if (Selected || (Focused && ShowFocusCues))
        {
            using var path = ClawTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 8);
            using var pen = new Pen(Selected ? ClawTheme.Accent : ClawTheme.Muted, Selected ? 2 : 1) { DashStyle = Selected ? DashStyle.Solid : DashStyle.Dot };
            g.DrawPath(pen, path);
        }
    }
}

/* Click, then press a combination: Ctrl, Alt, Shift and Win with one other key. */
internal sealed class KeyRecorder : ClawButton
{
    private bool recording;
    internal KeyCombo? Combo { get; private set; }
    internal event Action<KeyCombo>? Recorded;
    internal KeyRecorder() { Size = new(300, 44); Show(); Click += (_, _) => { recording = true; Show(); Focus(); }; LostFocus += (_, _) => { recording = false; Show(); }; }
    internal void Set(KeyCombo? combo) { Combo = combo; Show(); }
    private new void Show() { Text = recording ? L.Text("press_the_keys_now") : Combo?.Text ?? L.Text("click_then_press_keys"); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => recording || base.IsInputKey(keyData);
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!recording) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { recording = false; Show(); return true; }
        if (KeyCombo.IsModifier(key)) return true;
        bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
        Combo = new KeyCombo((keyData & Keys.Control) != 0, (keyData & Keys.Alt) != 0, (keyData & Keys.Shift) != 0, win, key);
        recording = false; Show(); Recorded?.Invoke(Combo); return true;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int key);
}

/* Everything about one slot: what it does, what it says, its icon and colour, and a way to try it. */
internal sealed class ShortcutInspector : Panel
{
    private readonly Func<ShortcutDeck> deck;
    private readonly Func<string> language;
    private readonly Action<ShortcutDeck> changed;
    private readonly Label error;
    private int slot;
    private IReadOnlyList<(string Name, string Path)>? apps;
    internal ShortcutInspector(Func<ShortcutDeck> deck, Func<string> displayLanguage, Action<ShortcutDeck> changed)
    {
        this.deck = deck; language = displayLanguage; this.changed = changed; BackColor = ClawTheme.Background;
        error = ClawTheme.Label("", 300, 9, false, 40); error.ForeColor = ClawTheme.Warning;
        // Rebuilt for a new width only; Bind itself sets the height.
        int boundWidth = -1;
        Resize += (_, _) => { if (Width != boundWidth) { boundWidth = Width; Bind(slot); } };
    }
    private sealed record ActionChoice(ShortcutAction Action) { public override string ToString() => Action.Name; }
    private sealed record AppChoice(string Name, string Path) { public override string ToString() => Name; }
    private ShortcutTile? Tile => deck().At(slot);
    private void Apply(ShortcutTile? tile)
    {
        var next = deck().Set(slot, tile);
        error.Text = "";
        try { next.Validate(); }
        catch (ArgumentException problem) { error.Text = problem.Message; }
        changed(next);
    }
    private int Width2 => Math.Max(240, Width - 4);
    private Label Caption(string text, int y) { var label = ClawTheme.Label(text, Width2, 9, true, 22); label.Location = new(0, y); Controls.Add(label); return label; }

    internal void Bind(int value)
    {
        slot = value;
        SuspendLayout();
        foreach (Control child in Controls.Cast<Control>().ToArray()) { Controls.Remove(child); if (child != error) child.Dispose(); }
        var tile = Tile;
        int y = 0, w = Width2;
        var heading = ClawTheme.Label(tile is null ? L.Text("empty_slot_n", slot + 1) : L.Text("tile_n", slot + 1), w, 13, true, 32); heading.Location = new(0, y); Controls.Add(heading); y += 40;
        Caption(L.Text("what_it_does"), y); y += 24;
        var action = new ChoiceBox { Width = w, Height = 44, Location = new(0, y) };
        foreach (var group in ShortcutCatalog.Groups) foreach (var item in ShortcutCatalog.Actions.Where(a => a.Group == group)) action.Items.Add(new ActionChoice(item));
        if (tile is not null) action.SelectedIndex = action.Items.FindIndex(i => ((ActionChoice)i).Action.Id == tile.Action);
        else action.Text = L.Text("choose_what_this_shortcut_does");
        action.SelectedIndexChanged += (_, _) =>
        {
            if (action.SelectedItem is not ActionChoice choice || choice.Action.Id == Tile?.Action) return;
            var kind = choice.Action;
            // Keep a target that still fits, so switching between app tiles keeps the app.
            var old = Tile;
            string target = old is not null && ShortcutCatalog.Get(old.Action).Target == kind.Target ? old.Target : "";
            Apply(ShortcutDeck.NewTile(slot, kind.Id, language(), target, old?.Keys ?? ""));
            BeginInvoke(() => Bind(slot));
        };
        Controls.Add(action); y += 52;
        if (tile is not null)
        {
            var kind = ShortcutCatalog.Get(tile.Action);
            y = Target(tile, kind, y, w);
            Caption(L.Text("label_on_the_display"), y); y += 24;
            var label = new ClawTextField { Width = w, Location = new(0, y), Text = tile.Label, PlaceholderText = L.Text("no_label_icon_only") };
            label.Editor.MaxLength = ShortcutDeck.MaxLabel;
            label.TextChanged += (_, _) => { if (Tile is { } current && current.Label != label.Text) Apply(current with { Label = label.Text }); };
            Controls.Add(label); y += 52;
            Caption(L.Text("icon"), y); y += 24;
            y = Icons(tile, y, w);
            Caption(L.Text("colour"), y); y += 24;
            y = Colors(tile, y, w);
            var tryIt = new ClawButton { Text = L.Text("try_it"), Size = new(132, 42), Location = new(0, y) };
            var tryTip = new ToolTip(); tryTip.SetToolTip(tryIt, L.Text("runs_this_shortcut_on_this_pc_now")); tryIt.Disposed += (_, _) => tryTip.Dispose();
            tryIt.Click += async (_, _) =>
            {
                if (Tile is not { } current) return;
                tryIt.Enabled = false;
                bool ok;
                try { deck().Validate(); ok = await ShortcutRunner.RunAsync(current, null, CancellationToken.None); }
                catch (ArgumentException problem) { error.Text = problem.Message; ok = false; }
                if (!IsDisposed && !tryIt.IsDisposed) { tryIt.Enabled = true; if (!ok && error.Text.Length == 0) error.Text = L.Text("that_shortcut_did_not_run_on_this_pc"); }
            };
            var remove = new ClawButton { Text = L.Text("remove"), Size = new(132, 42), Location = new(144, y) };
            remove.Click += (_, _) => { Apply(null); BeginInvoke(() => Bind(slot)); };
            Controls.AddRange([tryIt, remove]); y += 52;
        }
        else
        {
            var hint = ClawTheme.Label(L.Text("pick_an_action_to_fill_this_slot"), w, 9, false, 44); hint.Location = new(0, y); Controls.Add(hint); y += 48;
        }
        error.SetBounds(0, y, w, 44); Controls.Add(error); y += 48;
        Height = y;
        ResumeLayout();
    }

    private int Target(ShortcutTile tile, ShortcutAction kind, int y, int w)
    {
        switch (kind.Target)
        {
            case ShortcutTarget.App:
            {
                Caption(L.Text("app"), y); y += 24;
                var choice = new ChoiceBox { Width = w - 112, Height = 44, Location = new(0, y) };
                apps ??= AppIcons.StartMenuApps();
                foreach (var app in apps) choice.Items.Add(new AppChoice(app.Name, app.Path));
                int index = choice.Items.FindIndex(i => string.Equals(((AppChoice)i).Path, tile.Target, StringComparison.OrdinalIgnoreCase));
                if (index < 0 && tile.Target.Length > 0) { choice.Items.Insert(0, new AppChoice(Path.GetFileNameWithoutExtension(tile.Target), tile.Target)); index = 0; }
                if (index >= 0) choice.SelectedIndex = index; else choice.Text = L.Text("choose_an_app");
                choice.SelectedIndexChanged += (_, _) => { if (choice.SelectedItem is AppChoice app && Tile is { } current && current.Target != app.Path) Pick(current, app.Path); };
                var browse = new ClawButton { Text = L.Text("browse"), Size = new(104, 44), Location = new(w - 104, y) };
                browse.Click += (_, _) =>
                {
                    using var dialog = new OpenFileDialog { Filter = L.Text("apps_and_shortcuts_filter"), Title = L.Text("choose_an_app") };
                    if (dialog.ShowDialog(FindForm()) == DialogResult.OK && Tile is { } current) Pick(current, dialog.FileName);
                };
                Controls.AddRange([choice, browse]);
                return y + 52;
            }
            case ShortcutTarget.Website:
            {
                Caption(L.Text("web_address"), y); y += 24;
                var address = new ClawTextField { Width = w, Location = new(0, y), Text = tile.Target, PlaceholderText = "https://" };
                address.TextChanged += (_, _) =>
                {
                    if (Tile is not { } current || current.Target == address.Text) return;
                    // The label follows the address until someone writes their own.
                    bool following = current.Label == ShortcutDeck.NewTile(slot, current.Action, language(), current.Target).Label;
                    Apply(current with { Target = address.Text.Trim(), Label = following ? ShortcutDeck.NewTile(slot, current.Action, language(), address.Text.Trim()).Label : current.Label });
                };
                Controls.Add(address);
                return y + 52;
            }
            case ShortcutTarget.Folder:
            {
                Caption(L.Text("folder"), y); y += 24;
                var path = ClawTheme.Label(tile.Target.Length > 0 ? tile.Target : L.Text("no_folder_chosen"), w - 112, 9, false, 44); path.Location = new(0, y); path.AutoEllipsis = true;
                var choose = new ClawButton { Text = L.Text("browse"), Size = new(104, 44), Location = new(w - 104, y) };
                choose.Click += (_, _) =>
                {
                    using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = L.Text("choose_a_folder_for_this_shortcut"), InitialDirectory = tile.Target };
                    if (dialog.ShowDialog(FindForm()) == DialogResult.OK && Tile is { } current) Pick(current, dialog.SelectedPath);
                };
                Controls.AddRange([path, choose]);
                return y + 52;
            }
            case ShortcutTarget.Keys:
            {
                Caption(L.Text("key_combination"), y); y += 24;
                var recorder = new KeyRecorder { Width = w, Location = new(0, y) };
                recorder.Set(KeyCombo.Parse(tile.Keys));
                recorder.Recorded += combo =>
                {
                    if (Tile is not { } current) return;
                    bool following = current.Label == (KeyCombo.Parse(current.Keys)?.Text ?? "");
                    Apply(current with { Keys = combo.Value, Label = following ? ShortcutDeck.Fit(combo.Text) : current.Label });
                    BeginInvoke(() => Bind(slot));
                };
                var note = ClawTheme.Label(L.Text("keys_go_to_the_window_in_front"), w, 9, false, 40); note.Location = new(0, y + 48);
                Controls.AddRange([recorder, note]);
                return y + 92;
            }
        }
        return y;
    }
    // A new target renames a tile that still had its automatic name.
    private void Pick(ShortcutTile current, string target)
    {
        var fresh = ShortcutDeck.NewTile(slot, current.Action, language(), target);
        bool following = current.Label.Length == 0 || current.Label == ShortcutDeck.NewTile(slot, current.Action, language(), current.Target).Label;
        Apply(current with { Target = target, Label = following ? fresh.Label : current.Label });
        BeginInvoke(() => Bind(slot));
    }

    private int Icons(ShortcutTile tile, int y, int w)
    {
        int size = 40, gap = 6, perRow = Math.Max(4, (w + gap) / (size + gap)), i = 0;
        var ink = ShortcutArt.ColorOf(tile.Color);
        var choices = new List<ArtChoice>();
        if (tile.Action == "open_app")
            choices.Add(new ArtChoice("", false, L.Text("the_app_s_own_icon")) { Picture = AppIcons.Load(tile.Target), Selected = tile.AppIcon });
        // Each icon's lit twin shows up only when its tile is on.
        foreach (var icon in ShortcutArt.Icons.Where(i => ShortcutArt.Icons.All(o => o.On != i.Id)))
            choices.Add(new ArtChoice(icon.Id, false, icon.Id.Replace('_', ' ')) { Ink = ink, Selected = !tile.UsesAppIcon && icon.Id == tile.Icon });
        foreach (var choice in choices)
        {
            choice.Location = new(i % perRow * (size + gap), y + i / perRow * (size + gap)); ++i;
            choice.Click += (_, _) => { if (Tile is { } current) { Apply(choice.Value.Length == 0 ? current with { AppIcon = true } : current with { Icon = choice.Value, AppIcon = false }); BeginInvoke(() => Bind(slot)); } };
            Controls.Add(choice);
        }
        return y + (i + perRow - 1) / perRow * (size + gap) + 10;
    }
    private int Colors(ShortcutTile tile, int y, int w)
    {
        int size = 40, gap = 6, perRow = Math.Max(4, (w + gap) / (size + gap)), i = 0;
        foreach (var swatch in ShortcutArt.Colors)
        {
            var choice = new ArtChoice(swatch.Id, true, L.Text($"colour_{swatch.Id}")) { Selected = swatch.Id == tile.Color, Location = new(i % perRow * (size + gap), y + i / perRow * (size + gap)) }; ++i;
            choice.Click += (_, _) => { if (Tile is { } current) { Apply(current with { Color = choice.Value }); BeginInvoke(() => Bind(slot)); } };
            Controls.Add(choice);
        }
        return y + (i + perRow - 1) / perRow * (size + gap) + 14;
    }
}
