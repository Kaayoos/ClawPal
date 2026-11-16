using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClawPal.Companion;

/* One of the display's looks: its Home screen drawn in its own colours, its name and what it is like.
   The chosen one sits on an orange block. */
internal sealed class ThemeCard : ClawButton
{
    private static readonly Lazy<Bitmap?> crab = new(() =>
    {
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
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        var look = Themes.Get(ThemeId, AccentId);
        bool lit = Selected;
        ClawTheme.Fill(g, lit ? (Hot ? ClawTheme.AccentHover : ClawTheme.Accent) : Hot ? ClawTheme.Raised : ClawTheme.Surface, new Rectangle(0, 0, Width, Height));
        // The display in its slim case, wearing this look.
        int pad = LogicalToDeviceUnits(14);
        var area = new Rectangle(pad, pad, Width - 2 * pad, DeviceArt.HeightFor(Width - 2 * pad, .6f));
        var screen = DeviceArt.FitScreen(area, 800, 480);
        DeviceArt.Draw(g, screen, 800, 480, look.Background, canvas => PaintDisplay(canvas, screen, look));
        ClawTheme.Text(g, look.Name, ClawTheme.Font(ClawTheme.Head, true), lit ? ClawTheme.CaseInk : ClawTheme.Ink, new Rectangle(pad, area.Bottom + LogicalToDeviceUnits(10), Width - 2 * pad, LogicalToDeviceUnits(30)));
        ClawTheme.Text(g, look.Description, ClawTheme.Font(ClawTheme.Small), lit ? ClawTheme.CaseInk : ClawTheme.Muted, new Rectangle(pad, area.Bottom + LogicalToDeviceUnits(40), Width - 2 * pad, Height - area.Bottom - LogicalToDeviceUnits(44)),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ClawTheme.Frame(g, lit ? ClawTheme.CaseInk : ClawTheme.Ink, new Rectangle(3, 3, Width - 6, Height - 6));
    }
    /* An 800 x 480 home screen reduced to its essentials, drawn in the card's own palette. */
    private static void PaintDisplay(Graphics g, Rectangle r, ThemeLook look)
    {
        float s = r.Width / 800f;
        RectangleF R(float x, float y, float w, float h) => new(r.X + x * s, r.Y + y * s, w * s, h * s);
        ClawTheme.Fill(g, look.Background, r);
        using var rule = new Pen(look.Border, 1);
        using var ink = new SolidBrush(look.Ink); using var muted = new SolidBrush(look.Muted);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Cased looks set the time in the display's heavy face; this app's own Segoe UI Black stands in for it.
        string face = look.Cased ? "Segoe UI Black" : "Segoe UI Semibold";
        if (look.Mascot)
        {
            using var time = new Font(face, 92 * s, GraphicsUnit.Pixel);
            using var center = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("10:24", time, ink, R(20, 20, 400, 120), center);
            if (crab.Value is { } sprite)
            {
                var mode = g.InterpolationMode; var offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(sprite, R(124, 150, 192, 192)); g.InterpolationMode = mode; g.PixelOffsetMode = offset;
            }
            g.FillRectangle(muted, R(110, 386, 220, 14));
        }
        else
        {
            // A desk clock: the date as a heading, large light time, then the week with today marked in the accent.
            using var time = new Font(look.Cased ? "Segoe UI Black" : "Segoe UI Light", (look.Cased ? 118 : 136) * s, GraphicsUnit.Pixel);
            g.DrawString("10:24", time, ink, R(28, 116, 420, 170));
            g.FillRectangle(ink, R(44, 130, 150, 14));
            using var accent = new SolidBrush(look.Accent);
            for (int i = 0; i < 7; ++i)
            {
                g.FillRectangle(i == 3 ? ink : muted, R(44 + i * 50, 296, 30, 10));
                if (i == 3) g.FillRectangle(accent, R(44 + i * 50, 314, 30, look.Cased ? 6 : 4));
            }
        }
        if (look.Cased) { PaintCase(g, r, look); return; }
        g.DrawLine(rule, R(441, 40, 0, 0).Location, R(441, 420, 0, 0).Location);
        for (int i = 0; i < 4; ++i)
        {
            float y = 54 + i * 92;
            using var tile = ClawTheme.Round(R(466, y, 64, 64), 10 * s);
            if (look.Mascot) { using var fill = new SolidBrush(look.Surface); g.FillPath(fill, tile); }
            using (var outline = new Pen(look.Mascot ? look.Border : look.Accent, look.Mascot ? 1 : 1.2f)) g.DrawPath(outline, tile);
            g.FillRectangle(ink, R(552, y + 10, 160 - i * 18, 16)); g.FillRectangle(muted, R(552, y + 38, 110, 12));
            if (!look.Mascot && i < 3) g.DrawLine(rule, R(552, y + 78, 0, 0).Location, R(776, y + 78, 0, 0).Location);
        }
    }
    /* A cased look: square blocks for the shortcuts, then ClawPal's orange case around the screen with
     * its rail along the bottom, Apps and Home printed in the case's dark ink. */
    private static void PaintCase(Graphics g, Rectangle r, ThemeLook look)
    {
        float s = r.Width / 800f;
        RectangleF R(float x, float y, float w, float h) => new(r.X + x * s, r.Y + y * s, w * s, h * s);
        using var surface = new SolidBrush(look.Surface); using var ink = new SolidBrush(look.Ink); using var muted = new SolidBrush(look.Muted);
        using var accent = new SolidBrush(look.Accent); using var frame = new SolidBrush(look.Frame); using var frameInk = new SolidBrush(look.FrameInk);
        for (int i = 0; i < 4; ++i)
        {
            float y = 40 + i * 98;
            g.FillRectangle(surface, R(464, y, 308, 88));
            g.FillRectangle(accent, R(482, y + 20, 48, 48));
            g.FillRectangle(ink, R(552, y + 22, 150 - i * 18, 16)); g.FillRectangle(muted, R(552, y + 50, 104, 12));
        }
        g.FillRectangle(frame, R(0, 436, 800, 44));
        g.FillRectangle(frame, R(0, 0, 800, 8)); g.FillRectangle(frame, R(0, 0, 8, 480)); g.FillRectangle(frame, R(792, 0, 8, 480));
        for (int i = 0; i < 4; ++i) g.FillRectangle(frameInk, R(28 + i % 2 * 9, 450 + i / 2 * 9, 6, 6));
        g.FillRectangle(frameInk, R(52, 452, 58, 12)); g.FillRectangle(frameInk, R(704, 452, 70, 12));
    }
}

/* One accent as a block: a square of the colour and its name. The chosen one carries a tick. */
internal sealed class SwatchButton : ClawButton
{
    internal string AccentId { get; }
    internal SwatchButton(string accent) { AccentId = accent; Text = ""; Size = new(176, 64); AccessibleName = L.Text($"accent_{accent}"); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        var colour = Themes.AccentColor(AccentId);
        ClawTheme.Fill(g, Selected ? ClawTheme.Raised : Hot ? ClawTheme.Mix(ClawTheme.Surface, ClawTheme.Raised, .6f) : ClawTheme.Surface, new Rectangle(0, 0, Width, Height));
        int size = Math.Min(LogicalToDeviceUnits(32), Height - 24), top = (Height - size) / 2, left = LogicalToDeviceUnits(14);
        ClawTheme.Fill(g, colour, new Rectangle(left, top, size, size));
        if (Selected) ClawTheme.Tick(g, ClawTheme.CaseInk, new PointF(left + size / 2f, top + size / 2f), size * .7f, LogicalToDeviceUnits(3));
        ClawTheme.Text(g, L.Text($"accent_{AccentId}"), ClawTheme.Font(ClawTheme.Body, true), Selected ? ClawTheme.Ink : ClawTheme.Muted, new Rectangle(left + size + LogicalToDeviceUnits(12), 0, Width - size - left - LogicalToDeviceUnits(20), Height));
        if (Selected) ClawTheme.Fill(g, colour, new Rectangle(0, Height - LogicalToDeviceUnits(4), Width, LogicalToDeviceUnits(4)));
        PaintFocus(g);
    }
}

/* Two looks side by side, then Studio's accents. Raises Changed with the chosen theme and accent. */
internal sealed class LookPicker : Panel
{
    private readonly ThemeCard[] cards;
    private readonly SwatchButton[] swatches;
    private readonly Label accentTitle = ClawTheme.Label(L.Text("accent_colour"), 300, ClawTheme.Body, true, 30), accentNote = ClawTheme.Label("", 400, ClawTheme.Small, false, 30);
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
        // The cards name each accent; looks with none to choose say why.
        accentNote.Text = L.Text(look.Cased ? "accent_case_note" : "accent_crab_note"); accentNote.Visible = !look.HasAccents;
        LayoutPicker();
    }
    private void Choose(string theme, string accent)
    {
        if (theme == Theme && accent == Accent) return;
        SetLook(theme, accent); Changed?.Invoke(theme, accent);
    }
    private void LayoutPicker()
    {
        // Two looks to a row.
        int gap = 12, width = Math.Max(200, (ClientSize.Width - gap) / 2), card = DeviceArt.HeightFor(width - 28, .6f) + 14 + 92;
        for (int i = 0; i < cards.Length; i++) cards[i].SetBounds(i % 2 * (width + gap), i / 2 * (card + gap), width, card);
        int height = (cards.Length + 1) / 2 * (card + gap) - gap;
        accentTitle.SetBounds(0, height + 26, ClientSize.Width, 30);
        // Visible reads false until the window is shown, so ask the look instead.
        bool accents = Themes.Get(Theme, Accent).HasAccents;
        // Four blocks share a row while each fits its swatch and full name; narrower windows wrap them two by two,
        // so the eight accents always form full rows.
        int cardGap = 8, columns = (ClientSize.Width + cardGap) / (170 + cardGap) >= 4 ? Math.Min(4, swatches.Length) : 2;
        int cardWidth = (ClientSize.Width - cardGap * (columns - 1)) / columns, rows = (swatches.Length + columns - 1) / columns;
        for (int i = 0; i < swatches.Length; i++) swatches[i].SetBounds(i % columns * (cardWidth + cardGap), height + 62 + i / columns * (64 + cardGap), cardWidth, 64);
        accentNote.TextAlign = ContentAlignment.MiddleLeft;
        accentNote.SetBounds(0, height + 56, ClientSize.Width, 30);
        Height = accents ? height + 62 + rows * 64 + (rows - 1) * cardGap : height + 92;
    }
}
