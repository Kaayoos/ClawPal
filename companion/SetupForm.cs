using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClawPal.Companion;

public sealed record Place(string Name, string Country, string Region, double Latitude, double Longitude, string Timezone)
{
    public override string ToString() => string.Join(", ", new[] { Name, Region, Country }.Where(s => s.Length > 0));
}

internal sealed class CrabView : Control
{
    private readonly Dictionary<string, Bitmap> sheets = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 90 };
    private int frame;
    private string state = "idle";
    public string State { get => state; set { state = value; frame = 0; Invalidate(); } }
    public CrabView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor,true); BackColor=Color.Transparent;
        DoubleBuffered = true; Size = new(240, 240); Cursor = Cursors.Hand;
        var assembly = typeof(CrabView).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.Contains("crab_") && n.EndsWith(".png"))) {
            string key = name[(name.IndexOf("crab_") + 5)..].Replace("_sheet.png", "");
            using var stream = assembly.GetManifestResourceStream(name)!; using var image = new Bitmap(stream); sheets[key] = new Bitmap(image);
        }
        timer.Tick += (_, _) => { if (Visible && SystemInformation.IsMenuAnimationEnabled) { frame = (frame + 1) % 48; Invalidate(); } };
        timer.Start(); Click += async (_, _) => { var old = state; State = "happy"; await Task.Delay(3500); if (!IsDisposed && state == "happy") State = old; };
        AccessibleName = L.Text("clawpal_crab_click_to_say_hello");
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!sheets.TryGetValue(state, out var sheet)) return;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor; e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        int size = Math.Min(Width, Height) / 48 * 48;
        e.Graphics.DrawImage(sheet, new Rectangle((Width-size)/2,(Height-size)/2,size,size), new Rectangle(frame%8*48,frame/8*48,48,48), GraphicsUnit.Pixel);
    }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); foreach (var sheet in sheets.Values) sheet.Dispose(); } base.Dispose(disposing); }
}

internal sealed class SetupForm : ClawWindow
{
    /* Choosing the look comes first, so the rest of setup already wears it. */
    private enum Step { Look, Connect, Personalize, Companion, Review, Install }
    private readonly Configuration config;
    private readonly Connection connection;
    private readonly ClawScrollPanel content = new() { Dock = DockStyle.Fill, Padding = new Padding(44,6,40,16), MaximumContentWidth = 760 };
    private readonly ClawHeading title = new();
    private readonly Label subtitle = new(), status = new();
    private readonly StepList steps = new();
    private readonly Panel side = new() { Dock = DockStyle.Left, Width = 280, BackColor = ClawTheme.Surface };
    private readonly Label brand, tagline, version;
    private readonly ClawButton next, back;
    private readonly CrabView crab = new();
    private readonly LookPicker look;
    private readonly ChoiceBox pcLanguages=new(), displayLanguages=new();
    private readonly ChoiceBox ports = new(), places = new(), zones = new();
    private readonly ClawTextField ssid = new(), password = new(), city = new();
    private readonly WifiPicker wifi;
    private readonly ChoiceBox boards = new();
    private BoardDefinition SelectedBoard => (BoardDefinition?)boards.SelectedItem ?? BoardCatalog.Get(BoardCatalog.DefaultId);
    private readonly Label boardDescription = ClawTheme.Label("",400,9,false,20);
    private readonly ClawSwitch boardConfirmed = new(), fps = new(), auto = new(), startup = new(), backup = new() { Checked = true };
    private readonly Label deviceStatus = ClawTheme.Label("",400,9.5f,false,20);
    private readonly Label backupNote = ClawTheme.Label("",400,9,false,20);
    private DetectedDevice? detected;
    private UsbDevice? SelectedDevice => ports.SelectedItem as UsbDevice;
    private bool refreshingDevices, wifiLoaded;
    private int deviceTicks;
    private readonly ClawProgress progress = new() { Height = 8 };
    private readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 1000 };
    private Step step;
    private bool busy, installed;
    public bool Busy => busy;
    internal event Action? OpenSettings;
    internal void ReportProblem(string message) { crab.State="error"; status.Text=message; status.ForeColor=ClawTheme.Warning; Show(); Activate(); }
    private string? backupFolder;
    private Place? selected;
    private readonly bool preview;
    private Control[] Persistent => [look,boards,boardDescription,pcLanguages,displayLanguages,ports,places,zones,ssid,password,city,wifi,boardConfirmed,fps,auto,startup,backup,deviceStatus,backupNote,progress];
    public SetupForm(Configuration config, Connection connection, bool preview = false)
    {
        this.config = config; this.connection = connection; this.preview = preview;
        Text = L.Text("clawpal"); BackColor = ClawTheme.Background; ForeColor = ClawTheme.Ink; Font = ClawTheme.Font();
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(1040,760); MinimumSize = new(960,700); StartPosition = FormStartPosition.CenterScreen;
        using (var iconStream = typeof(SetupForm).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon")) if (iconStream != null) Icon = new Icon(iconStream);
        brand = ClawTheme.Label(L.Text("clawpal"),230,24,true,44); tagline = ClawTheme.Label(L.Text("a_little_company_for_your_desk_30d149"),230,10,false,24);
        version = ClawTheme.Label(L.Text("clawpal_94dbc7") + typeof(SetupForm).Assembly.GetName().Version?.ToString(3),230,9,false,24);
        side.Controls.AddRange([brand,tagline,steps,crab,version]); side.Resize += (_,_) => LayoutSide();
        var right = new Panel { Dock = DockStyle.Fill }; Controls.Add(right); Controls.Add(side);
        var header = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(44,30,40,0) };
        title.Dock = DockStyle.Top; title.Height = 46; title.Font = ClawTheme.Font(24,true); title.ForeColor = ClawTheme.Ink;
        subtitle.Dock = DockStyle.Fill; subtitle.ForeColor = ClawTheme.Muted; subtitle.Font = ClawTheme.Font(10.5f); subtitle.UseMnemonic = false;
        header.Controls.Add(subtitle); header.Controls.Add(title);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 84 };
        next = new ClawButton { Text = L.Text("continue"), Primary = true, Size = new(180,46), Anchor = AnchorStyles.Right | AnchorStyles.Top };
        back = new ClawButton { Text = L.Text("back"), Size = new(110,46), Anchor = AnchorStyles.Left | AnchorStyles.Top };
        status.ForeColor = ClawTheme.Muted; status.Font = ClawTheme.Font(9); status.UseMnemonic = false; status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.AddRange([next,back,status]);
        // Back, a short status line, then Continue; the line takes whatever room is between them.
        this.footer = footer; footer.Resize += (_,_) => LayoutFooter();
        var divider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClawTheme.Border };
        right.Controls.Add(content); right.Controls.Add(header); right.Controls.Add(divider); right.Controls.Add(footer);
        password.UseSystemPasswordChar = true; ssid.PlaceholderText = L.Text("new_2_4_ghz_wi_fi_name"); city.PlaceholderText = L.Text("search_a_city_or_village");
        wifi = new WifiPicker(ssid, preview) { BackColor = ClawTheme.Surface };
        foreach (var check in new[] { boardConfirmed, fps, auto, startup, backup }) check.Height = 40;
        boards.Items.AddRange(BoardCatalog.All.Cast<object>().ToArray()); boards.SelectedIndex=0;
        boards.SelectedIndexChanged += async (_,_) => { boardConfirmed.Checked=false; detected=null; boardDescription.Text=SelectedBoard.Description; UpdateContinue(); if(!preview)await CheckDevice(); };
        boardConfirmed.Text = L.Text("confirm_selected_board");
        boardDescription.Text=SelectedBoard.Description;
        boardConfirmed.CheckedChanged += (_,_) => UpdateContinue();
        backup.Text = L.Text("save_a_full_device_backup_before_installing_recommended");
        backup.CheckedChanged += (_,_) => UpdateBackupChoice();
        ports.SelectedIndexChanged += async (_,_) => { if (!refreshingDevices && !preview) { boardConfirmed.Checked=false; await CheckDevice(); } };
        fps.Text = L.Text("measure_game_fps_in_pc_monitor"); fps.Checked = config.TrackFps;
        auto.Text = L.Text("open_spotify_codex_on_the_display_when_they_launch"); auto.Checked = config.AutoOpenApps;
        startup.Text = L.Text("start_clawpal_with_windows"); startup.Checked = config.Token.Length == 0 || Startup.Enabled;
        foreach(var language in L.Languages){pcLanguages.Items.Add(language);displayLanguages.Items.Add(language);}
        pcLanguages.SelectedIndex=L.Languages.ToList().FindIndex(l=>l.Code==L.Normalize(config.CompanionLanguage));
        displayLanguages.SelectedIndex=L.Languages.ToList().FindIndex(l=>l.Code==L.Normalize(config.DisplaySettings?.Language));
        pcLanguages.SelectedIndexChanged+=(_,_)=>{if(pcLanguages.SelectedItem is LanguageChoice language){config.CompanionLanguage=language.Code;config.Save();L.SetLanguage(language.Code);}};
        L.Changed+=LanguageChanged;
        look = new LookPicker(config.DisplaySettings?.Theme ?? ClawTheme.Look.Id, config.DisplaySettings?.Accent ?? ClawTheme.Look.AccentId) { BackColor = ClawTheme.Surface };
        look.Changed += (theme, accent) => ClawTheme.Use(theme, accent);
        ClawTheme.Changed += LookChanged; LookChanged();
        TimeZones.Fill(zones, config.Timezone);
        if (config.City.Length > 0) { selected = new(config.City,"","",config.Latitude,config.Longitude,""); places.Items.Add(selected); places.SelectedIndex = 0; city.Text = config.City; }
        places.SelectedIndexChanged += (_, _) => { selected = places.SelectedItem as Place; if (selected is not null) TimeZones.SelectFor(zones, selected); };
        next.Click += async (_,_) => await Next(); back.Click += (_,_) => { if (step > Step.Look && !busy) { step--; ShowStep(); } };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = L.Text("keep_clawpal_open_until_the_usb_operation_finishes"); } };
        stateTimer.Tick += (_,_) => { if (step == Step.Install && installed) status.Text = connection.IsConnected ? L.Text("connected_over_wi_fi_your_desk_companion_is_ready") : L.Text("installed_connection", connection.Status); };
        stateTimer.Tick += async (_,_) => {
            if (!preview && step == Step.Connect && !busy && ++deviceTicks % 2 == 0) await RefreshDevices();
        };
        Shown += async (_,_) => { if (!preview) await RefreshDevices(true); };
        stateTimer.Start(); ShowStep();
    }
    private readonly Panel footer;
    private void LayoutFooter()
    {
        int pad = footer.LogicalToDeviceUnits(40), top = footer.LogicalToDeviceUnits(16);
        back.Location = new(pad, top); next.Location = new(footer.ClientSize.Width - next.Width - pad, top);
        int left = back.Visible ? back.Right + pad / 2 : pad; status.SetBounds(left, top - 4, Math.Max(40, next.Left - left - pad / 2), next.Height + 8);
        back.BringToFront(); next.BringToFront();
    }
    private string[] StepNames => [L.Text("language_and_look"), L.Text("connect_your_display"), L.Text("make_it_yours"), L.Text("companion_settings"), L.Text("review_install")];
    /* Brand at the top, steps under it, the version at the bottom, and the crab in whatever room is left between. */
    private void LayoutSide()
    {
        int pad = side.LogicalToDeviceUnits(30), w = side.ClientSize.Width - 2 * pad;
        brand.SetBounds(pad - 2, side.LogicalToDeviceUnits(28), w, side.LogicalToDeviceUnits(44));
        tagline.SetBounds(pad, brand.Bottom, w, side.LogicalToDeviceUnits(26));
        steps.SetBounds(pad, tagline.Bottom + side.LogicalToDeviceUnits(34), w, steps.Height);
        version.SetBounds(pad, side.ClientSize.Height - side.LogicalToDeviceUnits(40), w, side.LogicalToDeviceUnits(24));
        int room = version.Top - steps.Bottom - side.LogicalToDeviceUnits(24), size = Math.Min(side.LogicalToDeviceUnits(192), room) / 48 * 48;
        crab.Visible = ClawTheme.Mascot && size >= 96;
        if (crab.Visible) crab.SetBounds((side.ClientSize.Width - size) / 2, steps.Bottom + (room - size) / 2 + side.LogicalToDeviceUnits(12), size, size);
    }
    private ClawStack page = null!;
    /* Clears the page but keeps the inputs that live across steps, then starts a new stack of cards. */
    private ClawStack Cards()
    {
        foreach (var item in Persistent) item.Parent?.Controls.Remove(item);
        foreach (Control child in content.Content.Controls.Cast<Control>().ToArray()) child.Dispose();
        content.ResetScroll();
        page = new ClawStack { Dock = DockStyle.Top, Width = content.Content.Width, Gap = 16, BackColor = ClawTheme.Background, Padding = new Padding(0,4,0,8) };
        content.Content.Controls.Add(page); return page;
    }
    private static Label Note(string text, float size = 9) => ClawTheme.Label(text, 400, size, false, 20);
    private static Label Caption(string text) => ClawTheme.Label(text, 400, 10, true, 24);
    private static ClawRow Row(int stackBelow, params (Control control, int width)[] items) { var row = new ClawRow { StackBelow = stackBelow }; foreach (var (control, width) in items) row.Add(control, width); return row; }
    private static ClawStack Column(params Control[] items) { var column = new ClawStack { Gap = 8, BackColor = ClawTheme.Surface }; foreach (var item in items) column.Add(item); return column; }
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
        try {
            var devices = DeviceDiscovery.Find();
            if (!force && devices.SequenceEqual(ports.Items.Cast<UsbDevice>())) return;
            var previous = SelectedDevice;
            var chosen = DeviceDiscovery.Preferred(devices, previous);
            refreshingDevices = true;
            ports.Items.Clear(); ports.Items.AddRange(devices); ports.SelectedIndex = -1;
            if (chosen is not null) ports.SelectedItem = chosen;
            if (chosen != previous || force) {
                if (chosen != previous) boardConfirmed.Checked = false;
                detected = null;
                check = chosen is not null;
                deviceStatus.ForeColor = ClawTheme.Muted;
                deviceStatus.Text = devices.Length == 0
                    ? L.Text("no_usb_device_found_connect_the_display_with_a")
                    : L.Text("choose_your_display_above_to_check_its_chip_and");
                crab.State = "idle";
            }
        } catch (Exception e) { detected = null; deviceStatus.Text = L.Text("could_not_scan_usb_devices") + e.Message; crab.State = "error"; }
        finally { refreshingDevices = false; UpdateContinue(); }
        if (check) await CheckDevice();
    }
    private async Task CheckDevice()
    {
        detected = null;
        UpdateContinue();
        if (SelectedDevice is not { } device || busy) return;
        try {
            busy = true; content.Enabled = false; next.Enabled = false; crab.State = "working";
            deviceStatus.ForeColor = ClawTheme.Muted;
            deviceStatus.Text = L.Text("checking_the_connected_device");
            var installer = new Installer(new Progress<string>(s => { if (!IsDisposed) deviceStatus.Text = s; }));
            var result = await installer.Detect(device.Port,SelectedBoard.Id);
            if (!DeviceDiscovery.Find().Contains(device)) throw new IOException(L.Text("the_device_was_unplugged_reconnect_it_to_continue"));
            detected = result; crab.State = "idle"; deviceStatus.ForeColor = ClawTheme.Ink;
            deviceStatus.Text = L.Text("device_confirmed", result.Summary, result.DeviceId);
            status.Text = L.Text("usb_connection_checked_no_firmware_has_been_changed");
        } catch (Exception e) {
            crab.State = "error"; deviceStatus.ForeColor = ClawTheme.Warning;
            deviceStatus.Text = L.Text("device_check_failed_reconnect_the_usb_cable_or_close");
            status.Text = e.Message.Split('\n')[0];
        } finally { busy = false; content.Enabled = true; UpdateContinue(); }
    }
    private void ReportProgress(string message)
    {
        if (IsDisposed) return;
        status.Text = message;
        if (UsbToolOutput.Percent(message) is { } percent) { progress.Style = ProgressBarStyle.Continuous; progress.Value = percent; }
        else progress.Style = ProgressBarStyle.Marquee;
    }
    private void ShowStep()
    {
        var p = Cards(); status.Text = ""; status.ForeColor = ClawTheme.Muted; back.Visible = step is > Step.Look and < Step.Install; next.Enabled = true;
        steps.Set(StepNames, Math.Min((int)step, StepNames.Length)); LayoutSide(); LayoutFooter();
        crab.State = step == Step.Install ? "working" : "idle";
        next.Text = step == Step.Review ? L.Text("back_up_install") : step == Step.Install ? L.Text("keep_in_tray") : L.Text("continue");
        if (step == Step.Look) {
            title.Text = L.Text("welcome_to_clawpal"); subtitle.Text = L.Text("choose_a_look_subtitle");
            var lookCard = p.Add(new ClawCard(L.Text("look_section"))); lookCard.Add(look);
            var languages = p.Add(new ClawCard(L.Text("language")));
            languages.Add(Row(520,(Column(Caption(L.Text("pc_language")),pcLanguages),0),(Column(Caption(L.Text("display_language")),displayLanguages),0)));
            languages.Add(Note(L.Text("setup_languages_note")));
        } else if (step == Step.Connect) {
            title.Text = L.Text("meet_your_desk_companion"); subtitle.Text = L.Text("connect_your_display_with_a_usb_data_cable_to");
            var model = p.Add(new ClawCard(L.Text("display_model"))); model.Add(boards); model.Add(boardDescription);
            var usb = p.Add(new ClawCard(L.Text("connected_usb_display")));
            var scan = new ClawButton { Text = L.Text("check_again"), Height = 44 }; scan.Click += async (_,_) => await RefreshDevices(true);
            usb.Add(Row(0,(ports,0),(scan,160))); usb.Add(deviceStatus);
            var confirm = p.Add(new ClawCard());
            confirm.Add(boardConfirmed, 2); confirm.Add(Note(L.Text("usb_confirms_the_chip_and_flash_check_the_exact")));
            var restore = new ClawButton { Text = L.Text("restore_a_backup"), Height = 40 }; restore.Click += async (_,_) => await Restore();
            var recovery = p.Add(new ClawCard(L.Text("restore_backup_title"), L.Text("restore_backup_hint"))); recovery.Add(Row(0,(restore,220)));
            UpdateContinue();
        } else if (step == Step.Personalize) {
            title.Text = L.Text("make_yourself_at_home"); subtitle.Text = L.Text("your_pc_and_clawpal_should_share_the_same_local");
            var network = p.Add(new ClawCard(L.Text("wi_fi_card"), L.Text("wifi_24_only_note")));
            network.Add(wifi);
            network.Add(Row(520,(Column(Caption(L.Text("wi_fi_name_2_4_ghz")),ssid),0),(Column(Caption(L.Text("wi_fi_password")),password),0)));
            var reveal = new ClawSwitch { Text = L.Text("show_password"), Height = 36 }; reveal.CheckedChanged += (_,_) => password.UseSystemPasswordChar = !reveal.Checked; network.Add(reveal);
            if (!wifiLoaded) { wifiLoaded = true; _ = wifi.LoadAsync(); }
            var place = p.Add(new ClawCard(L.Text("weather_location"), L.Text("weather_location_hint")));
            var search = new ClawButton { Text = L.Text("find_location"), Height = 44 }; search.Click += async (_,_) => await Search(search);
            place.Add(Row(0,(city,0),(search,170)),8); place.Add(places);
            place.Add(Caption(L.Text("time_zone")),6); place.Add(zones);
        } else if (step == Step.Companion) {
            title.Text = L.Text("your_companion_your_rules"); subtitle.Text = L.Text("choose_how_clawpal_behaves_on_your_pc");
            var pc = p.Add(new ClawCard(L.Text("on_this_pc")));
            pc.Add(startup, 2); pc.Add(Note(L.Text("starts_quietly_in_the_tray_open_this_window_whenever")));
            pc.Add(auto, 2); pc.Add(Note(L.Text("when_an_enabled_app_opens_on_your_pc_show")));
            pc.Add(fps, 2); pc.Add(Note(L.Text("fps_uses_presentmon_to_read_windows_frame_events_it")));
            var ready = p.Add(new ClawCard(L.Text("ready_on_your_display"), L.Text("ready_on_your_display_hint")));
            var apps = new ClawWrap { ItemWidth = 176, ItemHeight = 48, Gap = 8 };
            foreach (var app in DisplayApps.All) apps.Controls.Add(new AppChip(app, false));
            ready.Add(apps);
        } else if (step == Step.Review) {
            title.Text = L.Text("ready_when_you_are"); subtitle.Text = L.Text("review_your_display_settings_and_backup_preference");
            var summary = p.Add(new ClawCard(SelectedBoard.ToString()));
            var chosen = Themes.Get(look.Theme, look.Accent);
            foreach (var (name, value) in new[] {
                (L.Text("review_firmware"), Installer.BundledVersion),
                (L.Text("review_usb"), $"{SelectedDevice?.Port} · ESP32-S3 · {SelectedBoard.FlashBytes/1048576} MB"),
                (L.Text("review_wifi"), ssid.Text),
                (L.Text("review_weather"), selected?.ToString() ?? ""),
                (L.Text("time_zone"), zones.SelectedItem?.ToString() ?? ""),
                (L.Text("language"), L.Text("review_languages", pcLanguages.SelectedItem, displayLanguages.SelectedItem)),
                (L.Text("look_section"), chosen.HasAccents ? chosen.Name + " · " + L.Text($"accent_{chosen.AccentId}") : chosen.Name) }) {
                var key = ClawTheme.Label(name, 160, 10, false, 22); var item = ClawTheme.Label(value, 300, 10, false, 22); item.ForeColor = ClawTheme.Ink;
                summary.Add(Row(0,(key,170),(item,0)), 6);
            }
            var safety = p.Add(new ClawCard(L.Text("backup_card"), L.Text("install_will_replace_the_current_device_firmware_and_its")));
            safety.Add(backup, 2); safety.Add(backupNote); UpdateBackupChoice();
            safety.Add(Note(L.Text("backups") + Installer.BackupsPath));
            var legal = p.Add(new ClawCard());
            legal.Add(Note(L.Text("install_license_notice")));
            var notices = new ClawButton { Text = L.Text("open_licenses"), Height = 40 };
            notices.Click += (_,_) => { if (preview) return; try { Installer.PreparePayload(); OpenFolder(Path.Combine(Installer.PayloadPath,"licenses")); } catch (Exception e) { ReportProblem(e.Message); } };
            legal.Add(Row(0,(notices,240)));
        } else {
            title.Text = L.Text("a_little_crab_work"); subtitle.Text = L.Text("clawpal_is_preparing_your_display");
            var work = p.Add(new ClawCard(backup.Checked ? L.Text("a_backup_first_then_a_fresh_start") : L.Text("a_fresh_start_for_your_display")));
            progress.Style = ProgressBarStyle.Marquee; work.Add(progress, 14);
            work.Add(Note(L.Text("keep_this_window_open_and_leave_your_display_connected"), 10));
            next.Enabled = false;
        }
    }
    private void LanguageChanged(){if(IsDisposed)return;L.RefreshControls(this);zones.Text=zones.SelectedItem?.ToString()??"";ShowStep();}
    /* Studio has no crab in the sidebar; the space simply stays calm. */
    private void LookChanged(){if(IsDisposed)return;look.SetLook(ClawTheme.Look.Id,ClawTheme.Look.AccentId);LayoutSide();Invalidate(true);}
    private SetupOptions Options() => new(SelectedDevice?.Port ?? "",ssid.Text,password.Text,selected?.Name ?? "",selected?.Latitude ?? double.NaN,selected?.Longitude ?? double.NaN,(zones.SelectedItem as TimeZoneChoice)?.Posix ?? "",fps.Checked,auto.Checked,startup.Checked,backup.Checked,((LanguageChoice)pcLanguages.SelectedItem!).Code,((LanguageChoice)displayLanguages.SelectedItem!).Code,SelectedBoard.Id,look.Theme,look.Accent);
    private async Task Next()
    {
        try {
            if (step == Step.Install) { if(installed) OpenSettings?.Invoke(); Close(); return; }
            if (busy) return;
            if (step == Step.Connect && (!boardConfirmed.Checked || detected is null || detected.Port != SelectedDevice?.Port)) throw new ArgumentException(L.Text("connect_and_check_your_display_then_confirm_the_board"));
            if (step == Step.Personalize || step == Step.Review) Options().Validate();
            if (step < Step.Review) { step++; ShowStep(); return; }
            var options = Options(); step = Step.Install; ShowStep(); busy = true; connection.SetPaused(true);
            var installer = new Installer(new Progress<string>(ReportProgress));
            backupFolder = await installer.Install(options,config,() => { connection.SetAutoOpenApps(config.AutoOpenApps); connection.SetPaused(config.Paused); });
            installed = true; busy = false; password.Clear(); crab.State = "happy"; progress.Style = ProgressBarStyle.Continuous; progress.Value = 100;
            title.Text = L.Text("clawpal_is_installed"); subtitle.Text = L.Text("the_firmware_has_been_written_and_verified");
            steps.Set(StepNames, StepNames.Length);
            var p = Cards();
            var done = p.Add(new ClawCard(backupFolder is not null ? L.Text("your_backup_is_safe") : L.Text("installed_without_a_backup_as_selected")));
            done.Add(Note(L.Text("clawpal_now_lives_in_your_tray_open_it_any"), 10));
            if (backupFolder is not null) { var folder = new ClawButton { Text = L.Text("open_backup_folder"), Height = 40 }; folder.Click += (_,_) => OpenFolder(backupFolder); done.Add(Row(0,(folder,220))); }
            var credits = p.Add(new ClawCard());
            credits.Add(Note(L.Text("weather_data_open_meteo_cc_by_4_0")));
            var about = new ClawButton { Text = L.Text("about_credits"), Height = 40 };
            about.Click += (_,_) => MessageBox.Show(this,L.Text("clawpal_94dbc7") + typeof(SetupForm).Assembly.GetName().Version?.ToString(3) + L.Text("original_crab_art_supplied_by_the_project_owner_weather"),L.Text("clawpal"));
            credits.Add(Row(0,(about,200)));
            next.Text=L.Text("open_settings"); next.Enabled = true;
        } catch (Exception e) {
            busy=false; connection.SetPaused(config.Paused); crab.State="error"; progress.Style=ProgressBarStyle.Continuous; status.Text=e.Message.Split('\n')[0]; status.ForeColor=ClawTheme.Warning; next.Enabled=true;
            if (step == Step.Install) {
                title.Text=L.Text("let_s_get_this_sorted"); subtitle.Text=L.Text("setup_did_not_finish_your_saved_backups_remain_available");
                var p = Cards(); var problem = p.Add(new ClawCard(L.Text("what_went_wrong")));
                problem.Add(Note(e.Message, 10));
                var backups = new ClawButton { Text = L.Text("open_backups"), Height = 40 }; backups.Click += (_,_) => OpenFolder(Installer.BackupsPath);
                var retry = new ClawButton { Text = L.Text("back_to_setup"), Height = 40 }; retry.Click += (_,_) => { step = Step.Connect; ShowStep(); };
                problem.Add(Row(0,(retry,200),(backups,200)));
                next.Text = L.Text("close");
            }
        }
    }
    private async Task Search(Button button)
    {
        button.Enabled=false; status.Text=L.Text("finding_locations");
        try {
            if(city.Text.Trim().Length < 2) throw new ArgumentException(L.Text("enter_at_least_two_letters"));
            using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(15) };
            using var json=JsonDocument.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=8&language="+Uri.EscapeDataString(L.Language)+"&name="+Uri.EscapeDataString(city.Text.Trim())));
            places.Items.Clear(); places.SelectedIndex=-1; selected=null;
            if(json.RootElement.TryGetProperty("results",out var results)) foreach(var p in results.EnumerateArray()) {
                string Str(string key)=>p.TryGetProperty(key,out var v)?v.GetString()??"":"";
                places.Items.Add(new Place(Str("name"),Str("country"),Str("admin1"),p.GetProperty("latitude").GetDouble(),p.GetProperty("longitude").GetDouble(),Str("timezone")));
            }
            if(places.Items.Count>0) places.SelectedIndex=0;
            status.Text=places.Items.Count==0?L.Text("no_match_try_a_nearby_town_or_different_spelling"):L.Text("choose_your_location_and_check_its_time_zone");
            crab.State=places.Items.Count==0?"error":"idle";
        } catch(Exception e) { crab.State="error"; status.Text=L.Text("location_search")+e.Message; } finally { button.Enabled=true; }
    }
    private async Task Restore()
    {
        if(busy || !boardConfirmed.Checked || detected is null || detected.Port != SelectedDevice?.Port) { status.Text=L.Text("connect_and_check_your_display_then_confirm_the_board");return; }
        using var dialog=new OpenFileDialog { Title=L.Text("choose_backup_json"),Filter=L.Text("clawpal_backup_metadata_backup_json"),InitialDirectory=Installer.BackupsPath };
        if(dialog.ShowDialog(this)!=DialogResult.OK) return;
        if(MessageBox.Show(this,L.Text("restore_this_backup_to_its_original_device_this_replaces"),L.Text("restore_backup"),MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        try { busy=true;content.Enabled=false;next.Enabled=false;connection.SetPaused(true);crab.State="working";
            await new Installer(new Progress<string>(s=>status.Text=s)).Restore(SelectedDevice!.Port,dialog.FileName,SelectedBoard.Id);
            status.Text=L.Text("backup_restored_exit_and_reopen_clawpal_to_reload_pairing");crab.State="happy";
        } catch(Exception e){crab.State="error";status.Text=e.Message;connection.SetPaused(config.Paused);}
        finally {busy=false;content.Enabled=true;next.Enabled=true;}
    }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); }
    internal void RenderPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        void Capture(string name){Application.DoEvents();using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,name+".png"),ImageFormat.Png);}
        ports.Items.Add(new UsbDevice("COM4", L.Text("espressif_usb_device"), "preview")); ports.SelectedIndex=0;
        detected=new("COM4","001122334455",16777216); boardConfirmed.Checked=true;
        deviceStatus.Text=detected.Summary + L.Text("example_device_usb_check_complete_confirm_the_model_below"); deviceStatus.ForeColor=ClawTheme.Ink;
        selected=new("Warsaw","Poland","",52.2297,21.0122,""); places.Items.Add(selected); places.SelectedIndex=0; city.Text="Warsaw";
        Show(); Application.DoEvents();
        for(int i=0;i<(int)Step.Install;i++){ step=(Step)i;ShowStep();Capture($"setup-{i}");if(content.MaximumOffset>0){content.ScrollTo(content.MaximumOffset);Capture($"setup-{i}-end");} }
        backup.Checked=false; ShowStep(); content.ScrollTo(content.MaximumOffset); Capture("setup-no-backup");
        backup.Checked=true; step=Step.Install; ShowStep(); ReportProgress(L.Text("backing_up_50_8_0_16_mb_03_00")); Capture("setup-backup-progress");
        ClientSize=new(960,660); step=Step.Look; ShowStep(); Capture("setup-small");
        Close();
    }
    protected override void Dispose(bool disposing) { if(disposing){L.Changed-=LanguageChanged;ClawTheme.Changed-=LookChanged;stateTimer.Dispose();foreach(var item in Persistent)if(item.Parent is null)item.Dispose();}base.Dispose(disposing); }
}
