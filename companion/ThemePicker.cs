using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClawPal.Companion;

/* A card showing a small display in its own look; the companion's current look frames it. */
internal sealed class ThemeCard : ClawButton
{
    private static readonly Lazy<Bitmap?> crab = new(() => {
        var assembly = typeof(ThemeCard).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("crab_idle_sheet.png"));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name)!; using var sheet = new Bitmap(stream);
        return sheet.Clone(new Rectangle(0, 0, 48, 48), sheet.PixelFormat);
    });
    internal string ThemeId { get; }
    internal string AccentId { get; set; } = Themes.DefaultAccent;
    internal ThemeCard(string theme) { ThemeId = theme; Text = Themes.Get(theme, null).Name; Size = new(292, 248); AccessibleDescription = Themes.Get(theme, null).Description; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Background);
        var look = Themes.Get(ThemeId, AccentId);
        ClawTheme.Fill(g, ClawTheme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), 12);
        using (var path = ClawTheme.Round(new RectangleF(Selected ? 1 : .5f, Selected ? 1 : .5f, Width - (Selected ? 3 : 2), Height - (Selected ? 3 : 2)), 12))
        using (var pen = new Pen(Selected ? ClawTheme.Accent : ClawTheme.Border, Selected ? 2 : 1)) g.DrawPath(pen, path);
        var screen = new Rectangle(12, 12, Width - 24, (Width - 24) * 3 / 5);
        PaintDisplay(g, screen, look);
        using var title = ClawTheme.Font(12, true); using var body = ClawTheme.Font(9);
        ClawTheme.Text(g, look.Name, title, ClawTheme.Ink, new Rectangle(14, screen.Bottom + 10, Width - 28, 28));
        ClawTheme.Text(g, look.Description, body, ClawTheme.Muted, new Rectangle(14, screen.Bottom + 38, Width - 28, Height - screen.Bottom - 44),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) { using var focus = ClawTheme.Round(new RectangleF(4, 4, Width - 9, Height - 9), 10); using var pen = new Pen(ClawTheme.Accent); pen.DashStyle = DashStyle.Dot; g.DrawPath(pen, focus); }
    }
    /* An 800 x 480 home screen reduced to its essentials, drawn in the card's own palette. */
    private static void PaintDisplay(Graphics g, Rectangle r, ThemeLook look)
    {
        float s = r.Width / 800f;
        RectangleF R(float x, float y, float w, float h) => new(r.X + x * s, r.Y + y * s, w * s, h * s);
        ClawTheme.Fill(g, look.Background, r, 8);
        using (var border = ClawTheme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), 8)) using (var pen = new Pen(look.Border)) g.DrawPath(pen, border);
        using var rule = new Pen(look.Border, 1);
        using var ink = new SolidBrush(look.Ink); using var muted = new SolidBrush(look.Muted);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (look.Mascot) {
            using var time = new Font("Segoe UI Semibold", 92 * s, GraphicsUnit.Pixel);
            using var center = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("10:24", time, ink, R(20, 20, 400, 120), center);
            if (crab.Value is { } sprite) {
                var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(sprite, R(124, 150, 192, 192)); g.InterpolationMode = mode; g.PixelOffsetMode = offset;
            }
            g.FillRectangle(muted, R(110, 386, 220, 14));
        } else {
            // A desk clock: the date as a heading, large light time, then the week with today marked in the accent.
            using var time = new Font("Segoe UI Light", 136 * s, GraphicsUnit.Pixel);
            g.DrawString("10:24", time, ink, R(28, 116, 420, 170));
            g.FillRectangle(ink, R(44, 130, 150, 14));
            using var accent = new SolidBrush(look.Accent);
            for (int i = 0; i < 7; ++i) {
                g.FillRectangle(i == 3 ? ink : muted, R(44 + i * 50, 296, 30, 10));
                if (i == 3) g.FillRectangle(accent, R(44 + i * 50, 314, 30, 4));
            }
        }
        g.DrawLine(rule, R(441, 40, 0, 0).Location, R(441, 420, 0, 0).Location);
        for (int i = 0; i < 4; ++i) {
            float y = 54 + i * 92;
            using var tile = ClawTheme.Round(R(466, y, 64, 64), 10 * s);
            if (look.Mascot) { using var fill = new SolidBrush(look.Surface); g.FillPath(fill, tile); }
            using (var outline = new Pen(look.Mascot ? look.Border : look.Accent, look.Mascot ? 1 : 1.2f)) g.DrawPath(outline, tile);
            g.FillRectangle(ink, R(552, y + 10, 160 - i * 18, 16)); g.FillRectangle(muted, R(552, y + 38, 110, 12));
            if (!look.Mascot && i < 3) g.DrawLine(rule, R(552, y + 78, 0, 0).Location, R(776, y + 78, 0, 0).Location);
        }
    }
}

/* One accent as a card: a large swatch and its name. The chosen one is outlined in its own colour
 * and its swatch carries a check. */
internal sealed class SwatchButton : ClawButton
{
    private bool hover;
    internal string AccentId { get; }
    internal SwatchButton(string accent) { AccentId = accent; Text = ""; Size = new(176, 72); AccessibleName = L.Text($"accent_{accent}"); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Background);
        var colour = Themes.AccentColor(AccentId);
        ClawTheme.Fill(g, hover && !Selected ? ClawTheme.Hover : ClawTheme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), 12);
        float inset = Selected ? 1 : .5f;
        using (var path = ClawTheme.Round(new RectangleF(inset, inset, Width - 1 - 2 * inset, Height - 1 - 2 * inset), 12))
        using (var pen = new Pen(Selected ? colour : ClawTheme.Border, Selected ? 2 : 1)) g.DrawPath(pen, path);
        int size = Math.Min(40, Height - 24), top = (Height - size) / 2;
        using (var fill = new SolidBrush(colour)) g.FillEllipse(fill, 16, top, size, size);
        if (Selected) {
            using var tick = new Pen(ClawTheme.Background, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float cx = 16 + size / 2f, cy = top + size / 2f, u = size / 40f;
            g.DrawLines(tick, new[] { new PointF(cx - 8 * u, cy), new PointF(cx - 2 * u, cy + 6 * u), new PointF(cx + 9 * u, cy - 6 * u) });
        }
        using var name = ClawTheme.Font(11, true);
        ClawTheme.Text(g, L.Text($"accent_{AccentId}"), name, Selected ? ClawTheme.Ink : ClawTheme.Muted, new Rectangle(size + 30, 0, Width - size - 40, Height));
        if (Focused && ShowFocusCues) { using var focus = ClawTheme.Round(new RectangleF(4, 4, Width - 9, Height - 9), 10); using var pen = new Pen(colour); pen.DashStyle = DashStyle.Dot; g.DrawPath(pen, focus); }
    }
}

/* Two looks side by side, then Studio's accents. Raises Changed with the chosen theme and accent. */
internal sealed class LookPicker : Panel
{
    private readonly ThemeCard[] cards;
    private readonly SwatchButton[] swatches;
    private readonly Label accentTitle = ClawTheme.Label(L.Text("accent_colour"), 300, 10, true, 30), accentNote = ClawTheme.Label("", 400, 9, false, 30);
    internal string Theme { get; private set; }
    internal string Accent { get; private set; }
    internal event Action<string, string>? Changed;
    internal LookPicker(string theme, string accent)
    {
        Theme = theme; Accent = accent; Height = 360;
        cards = Themes.Ids.Select(id => new ThemeCard(id)).ToArray();
        swatches = Themes.AccentIds.Select(id => new SwatchButton(id)).ToArray();
        foreach (var card in cards) card.Click += (_, _) => Choose(card.ThemeId, Accent);
        foreach (var swatch in swatches) swatch.Click += (_, _) => Choose(Theme, swatch.AccentId);
        Controls.AddRange(cards); Controls.AddRange(swatches); Controls.AddRange([accentTitle, accentNote]);
        Resize += (_, _) => LayoutPicker(); SetLook(theme, accent);
    }
    internal void SetLook(string theme, string accent)
    {
        Theme = theme; Accent = accent; var look = Themes.Get(theme, accent);
        foreach (var card in cards) { card.Selected = card.ThemeId == theme; card.AccentId = accent; card.Invalidate(); }
        foreach (var swatch in swatches) { swatch.Selected = swatch.AccentId == accent; swatch.Visible = look.HasAccents; swatch.Invalidate(); }
        // The cards name each accent; only the crab, which has none to choose, needs a note.
        accentNote.Text = L.Text("accent_crab_note"); accentNote.Visible = !look.HasAccents;
        LayoutPicker();
    }
    private void Choose(string theme, string accent)
    {
        if (theme == Theme && accent == Accent) return;
        SetLook(theme, accent); Changed?.Invoke(theme, accent);
    }
    private void LayoutPicker()
    {
        int gap = 16, width = Math.Max(200, (ClientSize.Width - gap) / 2), height = (width - 24) * 3 / 5 + 96;
        for (int i = 0; i < cards.Length; i++) cards[i].SetBounds(i * (width + gap), 0, width, height);
        accentTitle.SetBounds(0, height + 22, ClientSize.Width, 30);
        // Visible reads false until the window is shown, so ask the look instead.
        bool accents = Themes.Get(Theme, Accent).HasAccents;
        // Four cards share a row while each fits its swatch and full name; narrower windows wrap them two by two,
        // so the eight accents always form full rows.
        int cardGap = 12, columns = (ClientSize.Width + cardGap) / (170 + cardGap) >= 4 ? Math.Min(4, swatches.Length) : 2;
        int cardWidth = (ClientSize.Width - cardGap * (columns - 1)) / columns, rows = (swatches.Length + columns - 1) / columns;
        for (int i = 0; i < swatches.Length; i++) swatches[i].SetBounds(i % columns * (cardWidth + cardGap), height + 58 + i / columns * (72 + cardGap), cardWidth, 72);
        accentNote.TextAlign = ContentAlignment.MiddleLeft;
        accentNote.SetBounds(0, height + 54, ClientSize.Width, 40);
        Height = accents ? height + 66 + rows * 72 + (rows - 1) * cardGap : height + 100;
    }
}
