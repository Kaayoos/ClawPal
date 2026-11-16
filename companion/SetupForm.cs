using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ClawPal.Companion;

public sealed record Place(string Name, string Country, string Region, double Latitude, double Longitude, string Timezone)
{
    public override string ToString() => string.Join(", ", new[] { Name, Region, Country }.Where(s => s.Length > 0));
}

/* The crab, at a whole-number scale with hard pixel edges, playing one of its 48-frame sheets. */
internal sealed class CrabView : Control
{
    private static readonly Lazy<Dictionary<string, Bitmap>> sheets = new(() =>
    {
        var result = new Dictionary<string, Bitmap>(); var assembly = typeof(CrabView).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.Contains("crab_") && n.EndsWith(".png")))
        {
            string key = name[(name.IndexOf("crab_") + 5)..].Replace("_sheet.png", "");
            using var stream = assembly.GetManifestResourceStream(name)!; using var image = new Bitmap(stream); result[key] = new Bitmap(image);
        }
        return result;
    });
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 90 };
    private int frame;
    private string state = "idle";
    public string State { get => state; set { if (state == value) return; state = value; frame = 0; Invalidate(); } }
    public CrabView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); BackColor = Color.Transparent;
        Size = new(240, 240); Cursor = Cursors.Hand;
        timer.Tick += (_, _) => { if (Visible && ClawTheme.Motion) { frame = (frame + 1) % 48; Invalidate(); } };
        timer.Start(); Click += async (_, _) => { var old = state; State = "happy"; await Task.Delay(3500); if (!IsDisposed && state == "happy") State = old; };
        AccessibleName = L.Text("clawpal_crab_click_to_say_hello");
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!sheets.Value.TryGetValue(state, out var sheet)) return;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor; e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        int size = Math.Min(Width, Height) / 48 * 48;
        e.Graphics.DrawImage(sheet, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size), new Rectangle(frame % 8 * 48, frame / 8 * 48, 48, 48), GraphicsUnit.Pixel);
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

/* Setup's first screen, set like a poster: the whole case in orange, one short shout, the display itself
   stuck on at an angle, and a black strip along the bottom like the desk it will stand on. */
internal sealed class WelcomePoster : Panel
{
    private static readonly Lazy<Bitmap?> photo = new(() =>
    {
        using var stream = typeof(WelcomePoster).Assembly.GetManifestResourceStream("ClawPal.Companion.WelcomeDisplay");
        return stream is null ? null : new Bitmap(stream);
    });
    private readonly System.Windows.Forms.Timer shutter = new() { Interval = 15 };
    private float opened = 1;
    private long started;
    internal string Headline { get; set; } = "";
    internal string Sentence { get; set; } = "";
    /* Names written on the strip above the fields that sit there. */
    internal (string Text, Rectangle Field)[] Captions { get; set; } = [];
    internal CrabView Crab { get; } = new() { State = "happy" };
    /* Where the strip begins; controls placed on it sit on black. */
    internal int Seam => Height - BandHeight;
    internal int BandHeight => Math.Max(LogicalToDeviceUnits(150), Height * 27 / 100);
    public WelcomePoster()
    {
        BackColor = ClawTheme.Case; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(Crab);
        shutter.Tick += (_, _) =>
        {
            // The picture opens upward from the strip over a second, settling from a slight zoom.
            float t = Math.Min(1, (Environment.TickCount64 - started) / 1100f); opened = 1 - (1 - t) * (1 - t) * (1 - t);
            if (t >= 1) shutter.Stop(); Invalidate(PhotoBounds());
        };
    }
    internal void Reveal() { if (!ClawTheme.Motion) { opened = 1; Invalidate(); return; } opened = 0; started = Environment.TickCount64; shutter.Start(); }
    private RectangleF PhotoFrame()
    {
        // Stuck on across the seam, but only just: the strip's controls stay clear of it.
        float width = Math.Min(Width * .42f, LogicalToDeviceUnits(500)), height = width * (photo.Value is { } p ? p.Height / (float)p.Width : .86f);
        height = Math.Min(height, Seam - LogicalToDeviceUnits(40)); width = height / (photo.Value is { } q ? q.Height / (float)q.Width : .86f);
        float x = Width - width - LogicalToDeviceUnits(52), y = Seam - height * .9f;
        return new(x, y, width, height);
    }
    private Rectangle PhotoBounds() => Rectangle.Inflate(Rectangle.Round(PhotoFrame()), LogicalToDeviceUnits(40), LogicalToDeviceUnits(40));
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(ClawTheme.Case);
        ClawTheme.Fill(g, ClawTheme.Screen, new Rectangle(0, Seam, Width, Height - Seam));
        foreach (var (text, field) in Captions)
            ClawTheme.Text(g, text, ClawTheme.Font(ClawTheme.Small, true), ClawTheme.Muted, new Rectangle(field.X, field.Y - LogicalToDeviceUnits(28), field.Width, LogicalToDeviceUnits(24)), TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
        int pad = LogicalToDeviceUnits(40);
        TextRenderer.DrawText(g, L.Text("clawpal"), ClawTheme.Display(20), new Point(pad - 2, LogicalToDeviceUnits(24)), ClawTheme.CaseInk, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Headline, headlineFont, headlineBounds, ClawTheme.CaseInk, Block);
        TextRenderer.DrawText(g, Sentence, ClawTheme.Font(ClawTheme.Lead), sentenceBounds, ClawTheme.CaseInk, Block);
        var frame = PhotoFrame();
        if (photo.Value is { } picture)
        {
            var state = g.Save();
            float zoom = 1.12f - .12f * opened;
            g.TranslateTransform(frame.X + frame.Width / 2, frame.Y + frame.Height / 2); g.RotateTransform(-4);
            var local = new RectangleF(-frame.Width / 2, -frame.Height / 2, frame.Width, frame.Height);
            g.SetClip(new RectangleF(local.X, local.Bottom - local.Height * opened, local.Width, local.Height * opened), CombineMode.Replace);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(picture, new RectangleF(-frame.Width * zoom / 2, -frame.Height * zoom / 2, frame.Width * zoom, frame.Height * zoom));
            g.Restore(state);
        }
    }
    private const TextFormatFlags Block = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
    private Font headlineFont = ClawTheme.Display(30);
    private Rectangle headlineBounds, sentenceBounds;
    internal Rectangle HeadlineBounds => headlineBounds;
    internal Rectangle SentenceBounds => sentenceBounds;
    /* The tilted picture's outer bounds. */
    internal Rectangle PhotoArea
    {
        get
        {
            var f = PhotoFrame(); double a = 4 * Math.PI / 180;
            float w = (float)(f.Width * Math.Cos(a) + f.Height * Math.Sin(a)), h = (float)(f.Width * Math.Sin(a) + f.Height * Math.Cos(a));
            return Rectangle.Round(new RectangleF(f.X + f.Width / 2 - w / 2, f.Y + f.Height / 2 - h / 2, w, h));
        }
    }
    internal void SetWords(string headline, string sentence) { Headline = headline; Sentence = sentence; Arrange(); Invalidate(); }
    /* The headline as large as it can be while it keeps to two lines, in every language, and still
       leaves the sentence and a crab-sized space above the strip. */
    private void Arrange()
    {
        if (Width < 10 || Height < 10) return;
        int pad = LogicalToDeviceUnits(40), textWidth = Math.Max(LogicalToDeviceUnits(300), (int)PhotoFrame().X - pad - LogicalToDeviceUnits(24));
        int sentenceWidth = Math.Min(textWidth, LogicalToDeviceUnits(440)), crabRoom = LogicalToDeviceUnits(96);
        var body = ClawTheme.Font(ClawTheme.Lead);
        int sentenceHeight = TextRenderer.MeasureText(Sentence, body, new Size(sentenceWidth, int.MaxValue), Block).Height;
        for (float points = 60; points >= 28; points -= 2)
        {
            headlineFont = ClawTheme.Display(points);
            var size = TextRenderer.MeasureText(Headline, headlineFont, new Size(textWidth, int.MaxValue), Block);
            int line = TextRenderer.MeasureText("Ag", headlineFont, Size.Empty, TextFormatFlags.NoPadding).Height;
            int top = Math.Max(LogicalToDeviceUnits(84), Seam * 22 / 100);
            headlineBounds = new(pad - 3, top, textWidth, size.Height);
            sentenceBounds = new(pad, headlineBounds.Bottom + LogicalToDeviceUnits(18), sentenceWidth, sentenceHeight);
            if (size.Height <= line * 2 + 2 && Seam - sentenceBounds.Bottom - LogicalToDeviceUnits(12) >= crabRoom) break;
        }
        // The crab stands on the strip, under the words, as large as the room above the strip allows.
        int crab = Math.Clamp(Seam - sentenceBounds.Bottom - LogicalToDeviceUnits(12), 0, LogicalToDeviceUnits(144)) / 48 * 48;
        Crab.Visible = crab >= 48;
        Crab.SetBounds(pad - crab / 8, Seam - crab + crab / 12, crab, crab);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    /* The poster is part of the case: pressing anywhere on it moves the window, as the rail does. */
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && FindForm() is { } form) { ReleaseCapture(); SendMessage(form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
    }
    protected override void Dispose(bool disposing) { if (disposing) shutter.Dispose(); base.Dispose(disposing); }
}

internal sealed class SetupForm : ClawWindow
{
    /* Welcome first; the look waits until the display is connected, since this app wears its own. */
    private enum Step { Welcome, Connect, Network, Personalize, Review, Install }
    private readonly Configuration config;
    private readonly Connection connection;
    private readonly ClawScrollPanel content = new() { Dock = DockStyle.Fill, Padding = new Padding(40, 4, 28, 28), MaximumContentWidth = 820 };
    private readonly Panel header = new() { Dock = DockStyle.Top, Height = 132 }, footer = new() { Dock = DockStyle.Bottom, Height = 80, BackColor = ClawTheme.Surface };
    private readonly ClawHeading title = new();
    private readonly Label subtitle = ClawTheme.Label("", 600, ClawTheme.Body, false, 38), status = ClawTheme.Label("", 400, ClawTheme.Body, false, 40);
    private readonly StepList steps = new();
    private readonly CaseLabel brand = new() { Text = L.Text("clawpal"), Font = ClawTheme.Display(20) }, tagline = new() { Font = ClawTheme.Font(ClawTheme.Small) };
    private readonly Label version = new() { ForeColor = ClawTheme.CaseInk, Font = ClawTheme.Font(ClawTheme.Small), UseMnemonic = false };
    private readonly ClawButton next, back;
    private readonly CrabView crab = new() { Size = new(96, 96) };
    private readonly WelcomePoster poster = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ClawButton start = new() { Primary = true, OnScreen = true, Size = new(220, 56) };
    private readonly LookPicker look;
    private readonly ChoiceBox pcLanguages = new(), displayLanguages = new();
    private readonly ChoiceBox ports = new(), places = new(), zones = new();
    private readonly ClawTextField ssid = new(), password = new(), city = new();
    private readonly WifiPicker wifi;
    private readonly ChoiceBox boards = new();
    private BoardDefinition SelectedBoard => (BoardDefinition?)boards.SelectedItem ?? BoardCatalog.Get(BoardCatalog.DefaultId);
    private readonly Label boardDescription = ClawTheme.Label("", 400, ClawTheme.Small, false, 20);
    private readonly ClawSwitch boardConfirmed = new(), fps = new(), auto = new(), startup = new(), backup = new() { Checked = true };
    private readonly Label deviceStatus = ClawTheme.Label("", 400, ClawTheme.Body, false, 20);
    private readonly Label backupNote = ClawTheme.Label("", 400, ClawTheme.Small, false, 20);
    private readonly BigNumber percent = new() { Height = 82, Text = "" };
    private readonly StageStrip stages = new();
    private int stage;
    private string[] StageNames => [L.Text("stage_backup"), L.Text("stage_install"), L.Text("stage_check"), L.Text("stage_wifi")];
    private DetectedDevice? detected;
    private UsbDevice? SelectedDevice => ports.SelectedItem as UsbDevice;
    private bool refreshingDevices, wifiLoaded;
    private int deviceTicks;
    private readonly ClawProgress progress = new() { Height = 14 };
    private readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 1000 };
    private Step step;
    private bool busy, installed;
    public bool Busy => busy;
    internal event Action? OpenSettings;
    internal void ReportProblem(string message) { crab.State = "error"; status.Text = message; status.ForeColor = ClawTheme.Warning; Show(); Activate(); }
    private string? backupFolder;
    private Place? selected;
    private readonly bool preview;
    private Control[] Persistent => [look, boards, boardDescription, ports, places, zones, ssid, password, city, wifi, boardConfirmed, fps, auto, startup, backup, deviceStatus, backupNote, progress, percent, stages];
    public SetupForm(Configuration config, Connection connection, bool preview = false)
    {
        this.config = config; this.connection = connection; this.preview = preview;
        Text = L.Text("clawpal"); Font = ClawTheme.Font();
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(1080, 760); MinimumSize = new(960, 700); StartPosition = FormStartPosition.CenterScreen;
        using (var iconStream = typeof(SetupForm).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon")) if (iconStream != null) Icon = new Icon(iconStream);
        tagline.Text = L.Text("a_little_company_for_your_desk_30d149");
        version.Text = L.Text("clawpal_94dbc7") + typeof(SetupForm).Assembly.GetName().Version?.ToString(3);
        Rail.Controls.AddRange([brand, tagline, steps, version]); Rail.Resize += (_, _) => LayoutRail();
        next = new ClawButton { Text = L.Text("continue"), Primary = true, Size = new(190, 48) };
        back = new ClawButton { Text = L.Text("back"), Size = new(120, 48) };
        status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.AddRange([next, back, status]); footer.Resize += (_, _) => LayoutFooter();
        title.Text = " "; header.Controls.AddRange([title, subtitle, crab]); header.Resize += (_, _) => LayoutHeader(); title.TextChanged += (_, _) => LayoutHeader(); subtitle.TextChanged += (_, _) => LayoutHeader();
        ScreenArea.Controls.Add(content); ScreenArea.Controls.Add(header); ScreenArea.Controls.Add(footer); ScreenArea.Controls.Add(poster); poster.BringToFront();
        // The welcome strip holds the two languages and the way in.
        poster.Controls.AddRange([pcLanguages, displayLanguages, start]); poster.Resize += (_, _) => LayoutPoster();
        start.Click += async (_, _) => await Next();
        password.UseSystemPasswordChar = true; ssid.PlaceholderText = L.Text("new_2_4_ghz_wi_fi_name"); city.PlaceholderText = L.Text("search_a_city_or_village");
        wifi = new WifiPicker(ssid, preview);
        boards.Items.AddRange(BoardCatalog.All.Cast<object>().ToArray()); boards.SelectedIndex = 0;
        boards.SelectedIndexChanged += async (_, _) => { boardConfirmed.Checked = false; detected = null; boardDescription.Text = SelectedBoard.Description; UpdateContinue(); if (!preview) await CheckDevice(); };
        boardConfirmed.Text = L.Text("confirm_selected_board");
        boardDescription.Text = SelectedBoard.Description;
        boardConfirmed.CheckedChanged += (_, _) => UpdateContinue();
        backup.Text = L.Text("save_a_full_device_backup_before_installing_recommended");
        backup.CheckedChanged += (_, _) => UpdateBackupChoice();
        ports.SelectedIndexChanged += async (_, _) => { if (!refreshingDevices && !preview) { boardConfirmed.Checked = false; await CheckDevice(); } };
        fps.Text = L.Text("measure_game_fps_in_pc_monitor"); fps.Checked = config.TrackFps;
        auto.Text = L.Text("open_spotify_codex_on_the_display_when_they_launch"); auto.Checked = config.AutoOpenApps;
        startup.Text = L.Text("start_clawpal_with_windows"); startup.Checked = config.Token.Length == 0 || Startup.Enabled;
        foreach (var language in L.Languages) { pcLanguages.Items.Add(language); displayLanguages.Items.Add(language); }
        pcLanguages.SelectedIndex = L.Languages.ToList().FindIndex(l => l.Code == L.Normalize(config.CompanionLanguage));
        displayLanguages.SelectedIndex = L.Languages.ToList().FindIndex(l => l.Code == L.Normalize(config.DisplaySettings?.Language));
        pcLanguages.SelectedIndexChanged += (_, _) => { if (pcLanguages.SelectedItem is LanguageChoice language) { config.CompanionLanguage = language.Code; config.Save(); L.SetLanguage(language.Code); } };
        pcLanguages.AccessibleName = L.Text("pc_language"); displayLanguages.AccessibleName = L.Text("display_language");
        L.Changed += LanguageChanged;
        look = new LookPicker(config.DisplaySettings?.Theme ?? Themes.Default, config.DisplaySettings?.Accent ?? Themes.DefaultAccent);
        look.Changed += (theme, _) => BeginInvoke(() => L.SetVariant(theme));
        TimeZones.Fill(zones, config.Timezone);
        if (config.City.Length > 0) { selected = new(config.City, "", "", config.Latitude, config.Longitude, ""); places.Items.Add(selected); places.SelectedIndex = 0; city.Text = config.City; }
        places.SelectedIndexChanged += (_, _) => { selected = places.SelectedItem as Place; if (selected is not null) TimeZones.SelectFor(zones, selected); };
        next.Click += async (_, _) => await Next(); back.Click += (_, _) => { if (step > Step.Welcome && !busy) { step--; ShowStep(); } };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = L.Text("keep_clawpal_open_until_the_usb_operation_finishes"); } };
        stateTimer.Tick += (_, _) => { if (step == Step.Install && installed) status.Text = connection.IsConnected ? L.Text("connected_over_wi_fi_your_desk_companion_is_ready") : L.Text("installed_connection", connection.Status); };
        stateTimer.Tick += async (_, _) =>
        {
            if (!preview && step == Step.Connect && !busy && ++deviceTicks % 2 == 0) await RefreshDevices();
        };
        Shown += async (_, _) => { if (step == Step.Welcome && !preview) poster.Reveal(); if (!preview) await RefreshDevices(true); };
        stateTimer.Start(); ShowStep();
    }
    private void LayoutFooter()
    {
        int pad = footer.LogicalToDeviceUnits(28), top = (footer.ClientSize.Height - next.Height) / 2;
        back.Location = new(footer.LogicalToDeviceUnits(40), top); next.Location = new(footer.ClientSize.Width - next.Width - pad, top);
        int left = back.Visible ? back.Right + pad : footer.LogicalToDeviceUnits(40); status.SetBounds(left, 0, Math.Max(40, next.Left - left - pad), footer.ClientSize.Height);
        back.BringToFront(); next.BringToFront();
    }
    private void LayoutHeader()
    {
        int left = LogicalToDeviceUnits(40), right = LogicalToDeviceUnits(28), size = LogicalToDeviceUnits(96);
        crab.SetBounds(header.ClientSize.Width - right - size, LogicalToDeviceUnits(18), size, size);
        int width = Math.Max(120, crab.Left - left - LogicalToDeviceUnits(20));
        title.SetBounds(left, LogicalToDeviceUnits(30), width, title.Measure(width));
        subtitle.SetBounds(left + 1, title.Bottom + LogicalToDeviceUnits(8), width, ClawStack.TextHeight(subtitle, width));
        int height = Math.Max(crab.Bottom, subtitle.Bottom) + LogicalToDeviceUnits(20);
        if (header.Height != height) header.Height = height;
    }
    private void LayoutPoster()
    {
        int pad = LogicalToDeviceUnits(40), seam = poster.Seam, band = poster.Height - seam;
        start.SetBounds(poster.Width - start.Width - pad, seam + (band - start.Height) / 2, start.Width, start.Height);
        int width = Math.Min(LogicalToDeviceUnits(240), (start.Left - 2 * pad - LogicalToDeviceUnits(16)) / 2), top = seam + (band - LogicalToDeviceUnits(44)) / 2 + LogicalToDeviceUnits(12);
        pcLanguages.SetBounds(pad, top, width, LogicalToDeviceUnits(44)); displayLanguages.SetBounds(pad + width + LogicalToDeviceUnits(16), top, width, LogicalToDeviceUnits(44));
        poster.Captions = [(L.Text("pc_language"), pcLanguages.Bounds), (L.Text("display_language"), displayLanguages.Bounds)];
        poster.Invalidate();
    }
    private string[] StepNames => [L.Text("step_welcome"), L.Text("connect_your_display"), L.Text("step_network"), L.Text("make_it_yours"), L.Text("review_install")];
    /* The name at the top, the steps under it, and the version at the foot of the case. */
    private void LayoutRail()
    {
        int pad = Rail.LogicalToDeviceUnits(24), w = Rail.ClientSize.Width - 2 * pad;
        brand.SetBounds(pad - 2, Rail.LogicalToDeviceUnits(18), w, Rail.LogicalToDeviceUnits(44));
        tagline.SetBounds(pad, brand.Bottom, w, Rail.LogicalToDeviceUnits(40));
        steps.SetBounds(Rail.LogicalToDeviceUnits(12), Rail.LogicalToDeviceUnits(132), Rail.ClientSize.Width - Rail.LogicalToDeviceUnits(12), steps.Height);
        version.SetBounds(pad, Rail.ClientSize.Height - Rail.LogicalToDeviceUnits(42), w, Rail.LogicalToDeviceUnits(24));
    }
    private ClawStack page = null!;
    /* Clears the page but keeps the inputs that live across steps, then starts a new stack of sections. */
    private ClawStack Sections()
    {
        foreach (var item in Persistent) item.Parent?.Controls.Remove(item);
        foreach (Control child in content.Content.Controls.Cast<Control>().ToArray()) child.Dispose();
        content.ResetScroll();
        page = new ClawStack { Dock = DockStyle.Top, Width = content.Content.Width, Gap = 40, BackColor = ClawTheme.Screen, Padding = new Padding(0, 4, 0, 8) };
        content.Content.Controls.Add(page); return page;
    }
    private static Label Note(string text) => ClawTheme.Label(text, 400, ClawTheme.Small, false, 20);
    private static Label Caption(string text) => ClawTheme.Label(text, 400, ClawTheme.Body, true, 24);
    private static ClawRow Row(int stackBelow, params (Control control, int width)[] items) { var row = new ClawRow { StackBelow = stackBelow }; foreach (var (control, width) in items) row.Add(control, width); return row; }
    private static ClawStack Column(params Control[] items) { var column = new ClawStack { Gap = 8 }; foreach (var item in items) column.Add(item); return column; }
    private void UpdateContinue()
    {
        if (step == Step.Connect) next.Enabled = !busy && boardConfirmed.Checked && detected?.Port == SelectedDevice?.Port && detected is not null;
    }
    private void UpdateBackupChoice()
    {
        backupNote.Text = backup.Checked
            ? L.Text("a_full_backup_is_saved_and_verified_before_writing")
            : L.Text("no_backup_will_be_created_you_will_need_an");
        backupNote.ForeColor = backup.Checked ? ClawTheme.Muted : ClawTheme.Warning;
        if (step == Step.Review) next.Text = backup.Checked ? L.Text("back_up_install") : L.Text("install_569ca4");
    }
    private async Task RefreshDevices(bool force = false)
    {
        if (busy || refreshingDevices || preview) return;
        bool check = false;
        try
        {
            var devices = DeviceDiscovery.Find();
            if (!force && devices.SequenceEqual(ports.Items.Cast<UsbDevice>())) return;
            var previous = SelectedDevice;
            var chosen = DeviceDiscovery.Preferred(devices, previous);
            refreshingDevices = true;
            ports.Items.Clear(); ports.Items.AddRange(devices); ports.SelectedIndex = -1;
            if (chosen is not null) ports.SelectedItem = chosen;
            if (chosen != previous || force)
            {
                if (chosen != previous) boardConfirmed.Checked = false;
                detected = null;
                check = chosen is not null;
                deviceStatus.ForeColor = ClawTheme.Muted;
                deviceStatus.Text = devices.Length == 0
                    ? L.Text("no_usb_device_found_connect_the_display_with_a")
                    : L.Text("choose_your_display_above_to_check_its_chip_and");
                if (step != Step.Welcome) crab.State = "idle";
            }
        }
        catch (Exception e) { detected = null; deviceStatus.Text = L.Text("could_not_scan_usb_devices") + e.Message; crab.State = "error"; }
        finally { refreshingDevices = false; UpdateContinue(); }
        if (check) await CheckDevice();
    }
    private async Task CheckDevice()
    {
        detected = null;
        UpdateContinue();
        if (SelectedDevice is not { } device || busy) return;
        try
        {
            busy = true; content.Enabled = false; next.Enabled = false; crab.State = "thinking";
            deviceStatus.ForeColor = ClawTheme.Muted;
            deviceStatus.Text = L.Text("checking_the_connected_device");
            var installer = new Installer(new Progress<string>(s => { if (!IsDisposed) deviceStatus.Text = s; }));
            var result = await installer.Detect(device.Port, SelectedBoard.Id);
            if (!DeviceDiscovery.Find().Contains(device)) throw new IOException(L.Text("the_device_was_unplugged_reconnect_it_to_continue"));
            detected = result; crab.State = "happy"; deviceStatus.ForeColor = ClawTheme.Ink;
            deviceStatus.Text = L.Text("device_confirmed", result.Summary, result.DeviceId);
            status.Text = L.Text("usb_connection_checked_no_firmware_has_been_changed");
        }
        catch (Exception e)
        {
            crab.State = "error"; deviceStatus.ForeColor = ClawTheme.Warning;
            deviceStatus.Text = L.Text("device_check_failed_reconnect_the_usb_cable_or_close");
            status.Text = e.Message.Split('\n')[0];
        }
        finally { busy = false; content.Enabled = true; UpdateContinue(); }
    }
    private void ReportProgress(string message)
    {
        if (IsDisposed) return;
        status.Text = message;
        // The stage only moves forward: each message that opens a stage names it.
        int next = message == L.Text("backup_skipped_as_selected_preparing_installation") || message == L.Text("installing_clawpal_keep_usb_connected_until_this_finishes") ? 1
            : message == L.Text("checking_the_installed_firmware") ? 2 : message == L.Text("firmware_installed_waiting_for_clawpal_on_wi_fi") ? 3 : stage;
        if (next > stage) { stage = next; stages.Set(StageNames, stage); percent.Text = "…"; progress.Value = 0; }
        if (UsbToolOutput.Percent(message) is { } done) { progress.Style = ProgressBarStyle.Continuous; progress.Value = done; percent.Text = $"{done}%"; }
        else progress.Style = ProgressBarStyle.Marquee;
    }
    private static string Mood(Step step) => step switch { Step.Welcome => "happy", Step.Connect => "idle", Step.Network => "thinking", Step.Personalize => "happy", Step.Review => "carrying", _ => "working" };
    private void ShowStep()
    {
        bool welcome = step == Step.Welcome;
        // The welcome is a poster across the whole case; every other step is a page on the screen.
        poster.Visible = welcome; header.Visible = footer.Visible = content.Visible = !welcome; ShowRail(!welcome);
        var p = Sections(); status.Text = ""; status.ForeColor = ClawTheme.Muted; back.Visible = step is > Step.Welcome and < Step.Install; next.Enabled = true;
        // Installing is still "Review & install" in the rail; it ticks once the display answers.
        steps.Set(StepNames, Math.Min((int)step, StepNames.Length - 1)); LayoutRail(); LayoutFooter();
        crab.State = Mood(step);
        next.Text = step == Step.Review ? L.Text("back_up_install") : step == Step.Install ? L.Text("keep_in_tray") : L.Text("continue");
        if (welcome)
        {
            poster.SetWords(L.Text("welcome_headline"), L.Text("welcome_sentence")); start.Text = L.Text("lets_start");
            poster.Controls.AddRange([pcLanguages, displayLanguages]); LayoutPoster(); poster.Invalidate();
        }
        else if (step == Step.Connect)
        {
            title.Text = L.Text("meet_your_desk_companion"); subtitle.Text = L.Text("connect_your_display_with_a_usb_data_cable_to");
            var model = p.Add(new ClawSection(L.Text("display_model"))); model.Add(boards, 8); model.Add(boardDescription);
            var usb = p.Add(new ClawSection(L.Text("connected_usb_display")));
            var scan = new ClawButton { Text = L.Text("check_again") }; scan.Click += async (_, _) => await RefreshDevices(true);
            usb.Add(Row(0, (ports, 0), (scan, 170)), 10); usb.Add(deviceStatus);
            var confirm = p.Add(new ClawCard());
            confirm.Add(boardConfirmed, 4); confirm.Add(Note(L.Text("usb_confirms_the_chip_and_flash_check_the_exact")));
            var restore = new ClawButton { Text = L.Text("restore_a_backup") }; restore.Click += async (_, _) => await Restore();
            var recovery = p.Add(new ClawSection(L.Text("restore_backup_title"), L.Text("restore_backup_hint"))); recovery.Add(Row(0, (restore, 220)));
            UpdateContinue();
        }
        else if (step == Step.Network)
        {
            title.Text = L.Text("make_yourself_at_home"); subtitle.Text = L.Text("your_pc_and_clawpal_should_share_the_same_local");
            var network = p.Add(new ClawSection(L.Text("wi_fi_card"), L.Text("wifi_24_only_note")));
            network.Add(wifi, 16);
            network.Add(Row(520, (Column(Caption(L.Text("wi_fi_name_2_4_ghz")), ssid), 0), (Column(Caption(L.Text("wi_fi_password")), password), 0)));
            var reveal = new ClawSwitch { Text = L.Text("show_password") }; reveal.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !reveal.Checked; network.Add(reveal);
            if (!wifiLoaded) { wifiLoaded = true; _ = wifi.LoadAsync(); }
            var place = p.Add(new ClawSection(L.Text("weather_location"), L.Text("weather_location_hint")));
            var search = new ClawButton { Text = L.Text("find_location") }; search.Click += async (_, _) => await Search(search);
            place.Add(Row(0, (city, 0), (search, 170)), 8); place.Add(places, 22);
            place.Add(Caption(L.Text("time_zone")), 8); place.Add(zones);
        }
        else if (step == Step.Personalize)
        {
            title.Text = L.Text("your_companion_your_rules"); subtitle.Text = L.Text("make_it_yours_subtitle");
            var lookSection = p.Add(new ClawSection(L.Text("look_section"), L.Text("look_app_note"))); lookSection.Add(look);
            var pc = p.Add(new ClawSection(L.Text("on_this_pc")));
            pc.Add(startup, 2); pc.Add(Note(L.Text("starts_quietly_in_the_tray_open_this_window_whenever")), 16);
            pc.Add(auto, 2); pc.Add(Note(L.Text("when_an_enabled_app_opens_on_your_pc_show")), 16);
            pc.Add(fps, 2); pc.Add(Note(L.Text("fps_uses_presentmon_to_read_windows_frame_events_it")));
            var ready = p.Add(new ClawSection(L.Text("ready_on_your_display"), L.Text("ready_on_your_display_hint")));
            var apps = new ClawWrap { ItemWidth = 176, ItemHeight = 50, Gap = 8 };
            foreach (var app in DisplayApps.All) apps.Controls.Add(new AppChip(app, false));
            ready.Add(apps);
        }
        else if (step == Step.Review)
        {
            title.Text = L.Text("ready_when_you_are"); subtitle.Text = L.Text("review_your_display_settings_and_backup_preference");
            var chosen = Themes.Get(look.Theme, look.Accent);
            var summary = p.Add(new ClawCard(SelectedBoard.ToString()));
            summary.Add(new FactList(
                (L.Text("review_firmware"), Installer.BundledVersion),
                (L.Text("review_usb"), $"{SelectedDevice?.Port} · ESP32-S3 · {SelectedBoard.FlashBytes / 1048576} MB"),
                (L.Text("review_wifi"), ssid.Text),
                (L.Text("review_weather"), selected?.ToString() ?? ""),
                (L.Text("time_zone"), zones.SelectedItem?.ToString() ?? ""),
                (L.Text("language"), L.Text("review_languages", pcLanguages.SelectedItem, displayLanguages.SelectedItem)),
                (L.Text("look_section"), chosen.HasAccents ? chosen.Name + " · " + L.Text($"accent_{chosen.AccentId}") : chosen.Name)));
            var safety = p.Add(new ClawSection(L.Text("backup_card"), L.Text("install_will_replace_the_current_device_firmware_and_its")));
            safety.Add(backup, 4); safety.Add(backupNote, 10); UpdateBackupChoice();
            safety.Add(Note(L.Text("backups") + Installer.BackupsPath));
            var legal = p.Add(new ClawSection());
            legal.Add(Note(L.Text("install_license_notice")), 12);
            var notices = new ClawButton { Text = L.Text("open_licenses") };
            notices.Click += (_, _) => { if (preview) return; try { Installer.PreparePayload(); OpenFolder(Path.Combine(Installer.PayloadPath, "licenses")); } catch (Exception e) { ReportProblem(e.Message); } };
            legal.Add(Row(0, (notices, 240)));
        }
        else
        {
            title.Text = L.Text("a_little_crab_work"); subtitle.Text = L.Text("clawpal_is_preparing_your_display");
            var work = p.Add(new ClawSection(backup.Checked ? L.Text("a_backup_first_then_a_fresh_start") : L.Text("a_fresh_start_for_your_display")));
            stage = backup.Checked ? 0 : 1; stages.Set(StageNames, stage); work.Add(stages, 30);
            progress.Style = ProgressBarStyle.Marquee; percent.Text = "…"; work.Add(percent, 8); work.Add(progress, 18);
            work.Add(ClawTheme.Label(L.Text("keep_this_window_open_and_leave_your_display_connected"), 400, ClawTheme.Body, false, 22));
            next.Enabled = false;
        }
        LayoutHeader(); if (!welcome) content.Arrive();
    }
    private void LanguageChanged() { if (IsDisposed) return; L.RefreshControls(this); zones.Text = zones.SelectedItem?.ToString() ?? ""; tagline.Text = L.Text("a_little_company_for_your_desk_30d149"); ShowStep(); }
    private SetupOptions Options() => new(SelectedDevice?.Port ?? "", ssid.Text, password.Text, selected?.Name ?? "", selected?.Latitude ?? double.NaN, selected?.Longitude ?? double.NaN, (zones.SelectedItem as TimeZoneChoice)?.Posix ?? "", fps.Checked, auto.Checked, startup.Checked, backup.Checked, ((LanguageChoice)pcLanguages.SelectedItem!).Code, ((LanguageChoice)displayLanguages.SelectedItem!).Code, SelectedBoard.Id, look.Theme, look.Accent);
    private async Task Next()
    {
        try
        {
            if (step == Step.Install) { if (installed) OpenSettings?.Invoke(); Close(); return; }
            if (busy) return;
            if (step == Step.Connect && (!boardConfirmed.Checked || detected is null || detected.Port != SelectedDevice?.Port)) throw new ArgumentException(L.Text("connect_and_check_your_display_then_confirm_the_board"));
            if (step == Step.Network || step == Step.Review) Options().Validate();
            if (step < Step.Review) { step++; ShowStep(); return; }
            var options = Options(); step = Step.Install; ShowStep(); busy = true; connection.SetPaused(true);
            var installer = new Installer(new Progress<string>(ReportProgress));
            backupFolder = await installer.Install(options, config, () => { connection.SetAutoOpenApps(config.AutoOpenApps); connection.SetPaused(config.Paused); });
            installed = true; busy = false; password.Clear(); progress.Style = ProgressBarStyle.Continuous; progress.Value = 100;
            Finished();
        }
        catch (Exception e)
        {
            busy = false; connection.SetPaused(config.Paused); crab.State = "error"; progress.Style = ProgressBarStyle.Continuous; status.Text = e.Message.Split('\n')[0]; status.ForeColor = ClawTheme.Warning; next.Enabled = true;
            if (step == Step.Install) Failed(e.Message);
        }
    }
    private void Finished()
    {
        title.Text = L.Text("clawpal_is_installed"); subtitle.Text = L.Text("the_firmware_has_been_written_and_verified");
        steps.Set(StepNames, StepNames.Length); crab.State = "happy";
        var p = Sections();
        var done = p.Add(new ClawCard(backupFolder is not null ? L.Text("your_backup_is_safe") : L.Text("installed_without_a_backup_as_selected")));
        done.Add(ClawTheme.Label(L.Text("clawpal_now_lives_in_your_tray_open_it_any"), 400, ClawTheme.Body, false, 22), 14);
        if (backupFolder is not null) { var folder = new ClawButton { Text = L.Text("open_backup_folder") }; var shown = backupFolder; folder.Click += (_, _) => OpenFolder(shown); done.Add(Row(0, (folder, 230))); }
        var credits = p.Add(new ClawSection());
        credits.Add(Note(L.Text("weather_data_open_meteo_cc_by_4_0")), 12);
        var about = new ClawButton { Text = L.Text("about_credits") };
        about.Click += (_, _) => MessageBox.Show(this, L.Text("clawpal_94dbc7") + typeof(SetupForm).Assembly.GetName().Version?.ToString(3) + L.Text("original_crab_art_supplied_by_the_project_owner_weather"), L.Text("clawpal"));
        credits.Add(Row(0, (about, 210)));
        next.Text = L.Text("open_settings"); next.Enabled = true; LayoutHeader();
    }
    private void Failed(string message)
    {
        title.Text = L.Text("let_s_get_this_sorted"); subtitle.Text = L.Text("setup_did_not_finish_your_saved_backups_remain_available");
        steps.Set(StepNames, StepNames.Length - 1); crab.State = "error";
        var p = Sections(); var problem = p.Add(new ClawCard(L.Text("what_went_wrong")));
        problem.Add(ClawTheme.Label(message, 400, ClawTheme.Body, false, 22), 14);
        var backups = new ClawButton { Text = L.Text("open_backups") }; backups.Click += (_, _) => OpenFolder(Installer.BackupsPath);
        var retry = new ClawButton { Text = L.Text("back_to_setup") }; retry.Click += (_, _) => { step = Step.Connect; ShowStep(); };
        problem.Add(Row(0, (retry, 210), (backups, 210)));
        next.Text = L.Text("close"); LayoutHeader();
    }
    private async Task Search(Button button)
    {
        button.Enabled = false; status.Text = L.Text("finding_locations"); crab.State = "thinking";
        try
        {
            if (city.Text.Trim().Length < 2) throw new ArgumentException(L.Text("enter_at_least_two_letters"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var json = JsonDocument.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=8&language=" + Uri.EscapeDataString(L.Language) + "&name=" + Uri.EscapeDataString(city.Text.Trim())));
            places.Items.Clear(); places.SelectedIndex = -1; selected = null;
            if (json.RootElement.TryGetProperty("results", out var results)) foreach (var p in results.EnumerateArray())
                {
                    string Str(string key) => p.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
                    places.Items.Add(new Place(Str("name"), Str("country"), Str("admin1"), p.GetProperty("latitude").GetDouble(), p.GetProperty("longitude").GetDouble(), Str("timezone")));
                }
            if (places.Items.Count > 0) places.SelectedIndex = 0;
            status.Text = places.Items.Count == 0 ? L.Text("no_match_try_a_nearby_town_or_different_spelling") : L.Text("choose_your_location_and_check_its_time_zone");
            crab.State = places.Items.Count == 0 ? "error" : "sunny";
        }
        catch (Exception e) { crab.State = "error"; status.Text = L.Text("location_search") + e.Message; }
        finally { button.Enabled = true; }
    }
    private async Task Restore()
    {
        if (busy || !boardConfirmed.Checked || detected is null || detected.Port != SelectedDevice?.Port) { status.Text = L.Text("connect_and_check_your_display_then_confirm_the_board"); return; }
        using var dialog = new OpenFileDialog { Title = L.Text("choose_backup_json"), Filter = L.Text("clawpal_backup_metadata_backup_json"), InitialDirectory = Installer.BackupsPath };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, L.Text("restore_this_backup_to_its_original_device_this_replaces"), L.Text("restore_backup"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            busy = true; content.Enabled = false; next.Enabled = false; connection.SetPaused(true); crab.State = "working";
            await new Installer(new Progress<string>(s => status.Text = s)).Restore(SelectedDevice!.Port, dialog.FileName, SelectedBoard.Id);
            status.Text = L.Text("backup_restored_exit_and_reopen_clawpal_to_reload_pairing"); crab.State = "happy";
        }
        catch (Exception e) { crab.State = "error"; status.Text = e.Message; connection.SetPaused(config.Paused); }
        finally { busy = false; content.Enabled = true; next.Enabled = true; }
    }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    internal void RenderPreviews(string directory)
    {
        ClawTheme.Still = true; Directory.CreateDirectory(directory);
        void Capture(string name) { Application.DoEvents(); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png); }
        ports.Items.Add(new UsbDevice("COM4", L.Text("espressif_usb_device"), "preview")); ports.SelectedIndex = 0;
        detected = new("COM4", "001122334455", 16777216); boardConfirmed.Checked = true;
        deviceStatus.Text = detected.Summary + L.Text("example_device_usb_check_complete_confirm_the_model_below"); deviceStatus.ForeColor = ClawTheme.Ink;
        selected = new("Warsaw", "Poland", "", 52.2297, 21.0122, ""); places.Items.Add(selected); places.SelectedIndex = 0; city.Text = "Warsaw";
        Show(); Application.DoEvents();
        for (int i = 0; i < (int)Step.Install; i++) { step = (Step)i; ShowStep(); Capture($"setup-{i}"); if (content.Visible && content.MaximumOffset > 0) { content.ScrollTo(content.MaximumOffset); Capture($"setup-{i}-end"); } }
        backup.Checked = false; ShowStep(); content.ScrollTo(content.MaximumOffset); Capture("setup-no-backup");
        backup.Checked = true; step = Step.Install; ShowStep(); ReportProgress(L.Text("backing_up_50_8_0_16_mb_03_00")); Capture("setup-backup-progress");
        installed = true; backupFolder = Installer.BackupsPath; Finished(); Capture("setup-done");
        Failed("Simulated problem: the display stopped answering on COM4."); crab.State = "error"; Capture("setup-error");
        installed = false; ClientSize = new(960, 700); step = Step.Welcome; ShowStep(); Capture("setup-small");
        step = Step.Connect; ShowStep(); Capture("setup-small-connect");
        Close();
    }
    protected override void Dispose(bool disposing) { if (disposing) { L.Changed -= LanguageChanged; stateTimer.Dispose(); foreach (var item in Persistent) if (item.Parent is null) item.Dispose(); } base.Dispose(disposing); }
}
