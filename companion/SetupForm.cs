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
    internal static readonly Color Ground = Color.FromArgb(16,16,16), Surface = Color.FromArgb(29,29,29), Ink = Color.FromArgb(245,241,235), Muted = Color.FromArgb(181,175,168), Orange = Color.FromArgb(255,139,79);
    private readonly Configuration config;
    private readonly Connection connection;
    private readonly ClawScrollPanel content = new() { Dock = DockStyle.Fill, Padding = new Padding(40,30,40,20) };
    private readonly Label title = new(), subtitle = new(), status = new(), stepText = new();
    private readonly Button next, back;
    private readonly CrabView crab = new();
    private readonly ChoiceBox pcLanguages=new(), displayLanguages=new();
    private readonly ChoiceBox ports = new(), places = new(), zones = new();
    private readonly ClawTextField ssid = new(), password = new(), city = new();
    private readonly ChoiceBox boards = new();
    private BoardDefinition SelectedBoard => (BoardDefinition?)boards.SelectedItem ?? BoardCatalog.Get(BoardCatalog.DefaultId);
    private readonly Label boardDescription = new() { Height=66, AutoSize=false };
    private readonly ClawSwitch boardConfirmed = new(), fps = new(), auto = new(), startup = new(), backup = new() { Checked = true };
    private readonly Label deviceStatus = new() { Width = 600, Height = 58, ForeColor = Muted };
    private readonly Label backupNote = new() { Width = 600, Height = 60, ForeColor = Muted };
    private DetectedDevice? detected;
    private UsbDevice? SelectedDevice => ports.SelectedItem as UsbDevice;
    private bool refreshingDevices;
    private int deviceTicks;
    private readonly ProgressBar progress = new();
    private readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 1000 };
    private int step;
    private bool busy, installed;
    public bool Busy => busy;
    internal event Action? OpenSettings;
    internal void ReportProblem(string message) { crab.State="error"; status.Text=message; Show(); Activate(); }
    private string? backupFolder;
    private Place? selected;
    private readonly bool preview;
    public SetupForm(Configuration config, Connection connection, bool preview = false)
    {
        this.config = config; this.connection = connection; this.preview = preview;
        Text = L.Text("clawpal"); BackColor = Ground; ForeColor = Ink; Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(1000,740); MinimumSize = new(970,770); StartPosition = FormStartPosition.CenterScreen;
        using (var iconStream = typeof(SetupForm).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon")) if (iconStream != null) Icon = new Icon(iconStream);
        var side = new Panel { Dock = DockStyle.Left, Width = 285, BackColor = Surface, Padding = new Padding(30) };
        var brand = Label(L.Text("clawpal"), 27, true); brand.Location = new(30,35); brand.AutoSize = true; side.Controls.Add(brand);
        var tagline = Label(L.Text("a_little_company_for_your_desk_30d149"), 10); tagline.Location = new(32,89); tagline.Size = new(235,50); side.Controls.Add(tagline);
        crab.Location = new(22,220); crab.Anchor = AnchorStyles.Left | AnchorStyles.Top; side.Controls.Add(crab);
        stepText.Location = new(32,495); stepText.Size = new(220,180); stepText.ForeColor = Muted; stepText.Font = new Font("Segoe UI", 11); side.Controls.Add(stepText);
        var version = Label(L.Text("clawpal_94dbc7") + typeof(SetupForm).Assembly.GetName().Version?.ToString(3), 9); version.Dock = DockStyle.Bottom; version.Height = 25; side.Controls.Add(version);
        var right = new Panel { Dock = DockStyle.Fill }; Controls.Add(right); Controls.Add(side);
        var header = new Panel { Dock = DockStyle.Top, Height = 130, Padding = new Padding(40,32,32,0) };
        title.Dock = DockStyle.Top; title.Height = 47; title.Font = new Font("Segoe UI", 25, FontStyle.Bold); title.ForeColor = Ink;
        subtitle.Dock = DockStyle.Fill; subtitle.ForeColor = Muted;
        header.Controls.Add(subtitle); header.Controls.Add(title);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 120, Padding = new Padding(40,8,40,24) };
        next = Button(L.Text("continue"), true); next.Size = new(170,44); next.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; next.Location = new(505,52);
        back = Button(L.Text("back")); back.Size = new(100,44); back.Location = new(40,52); back.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        status.SetBounds(40,3,620,43); status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; status.ForeColor = Muted; status.Font = new Font("Segoe UI",9);
        footer.Controls.AddRange([next,back,status]); footer.Resize += (_,_) => next.Left = footer.ClientSize.Width-next.Width-40;
        right.Controls.Add(content); right.Controls.Add(header); right.Controls.Add(footer);
        foreach (var box in new[] { ports, places, zones }) box.Width = 600;
        foreach (var box in new[] { ssid, password, city }) { box.BackColor = Surface; box.ForeColor = Ink;  box.Width = 600; box.Font = new Font("Segoe UI",12); }
        password.UseSystemPasswordChar = true;
        foreach (var check in new[] { boardConfirmed, fps, auto, startup, backup }) { check.ForeColor = Ink; check.AutoSize = false; check.Size = new(600,40); check.FlatStyle = FlatStyle.Standard; }
        boards.Items.AddRange(BoardCatalog.All.Cast<object>().ToArray()); boards.SelectedIndex=0;
        boards.SelectedIndexChanged += async (_,_) => { boardConfirmed.Checked=false; detected=null; boardDescription.Text=SelectedBoard.Description; UpdateContinue(); if(!preview)await CheckDevice(); };
        boardConfirmed.Text = L.Text("confirm_selected_board");
        boardDescription.ForeColor=Muted; boardDescription.Text=SelectedBoard.Description;
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
        TimeZones.Fill(zones, config.Timezone);
        if (config.City.Length > 0) { selected = new(config.City,"","",config.Latitude,config.Longitude,""); places.Items.Add(selected); places.SelectedIndex = 0; city.Text = config.City; }
        places.SelectedIndexChanged += (_, _) => { selected = places.SelectedItem as Place; if (selected is not null) TimeZones.SelectFor(zones, selected); };
        next.Click += async (_,_) => await Next(); back.Click += (_,_) => { if (step > 0 && !busy) { step--; ShowStep(); } };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = L.Text("keep_clawpal_open_until_the_usb_operation_finishes"); } };
        stateTimer.Tick += (_,_) => { if (step == 4 && installed) status.Text = connection.IsConnected ? L.Text("connected_over_wi_fi_your_desk_companion_is_ready") : L.Text("installed_connection", connection.Status); };
        stateTimer.Tick += async (_,_) => {
            if (!preview && step == 0 && !busy && ++deviceTicks % 2 == 0) await RefreshDevices();
        };
        Shown += async (_,_) => { if (!preview) await RefreshDevices(true); };
        stateTimer.Start(); ShowStep();
    }
    private static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? Ink : Muted, AutoSize = false, Height = 38, Width = 610 };
    private static Button Button(string text, bool primary = false)
    {
        var b = new ClawButton { Primary=primary, Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Orange : Surface, ForeColor = primary ? Ground : Ink, Cursor = Cursors.Hand, Height = 40, Width = 155, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255,165,111) : Color.FromArgb(49,49,49); return b;
    }
    private FlowLayoutPanel Flow()
    {
        foreach(Control child in content.Content.Controls.Cast<Control>().ToArray()){foreach(Control item in new Control[]{boards,boardDescription,pcLanguages,displayLanguages,ports,places,zones,ssid,password,city,boardConfirmed,fps,auto,startup,backup,deviceStatus,backupNote,progress})if(child.Contains(item))item.Parent=null;child.Dispose();}
        content.ResetScroll();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        content.Content.Controls.Add(flow);
        flow.SizeChanged+=(_,_)=>{foreach(Control item in flow.Controls){if(item is not System.Windows.Forms.Button || item is ChoiceBox || item is ClawSwitch)item.Width=Math.Max(1,flow.ClientSize.Width-item.Margin.Horizontal);}};
        return flow;
    }
    private static void Add(FlowLayoutPanel flow, Control control, int after = 14) { control.Margin = new Padding(0,0,0,after); flow.Controls.Add(control);if(control is not System.Windows.Forms.Button || control is ChoiceBox || control is ClawSwitch)control.Width=Math.Max(1,flow.ClientSize.Width-control.Margin.Horizontal); }
    private void UpdateContinue()
    {
        if (step == 0) next.Enabled = !busy && boardConfirmed.Checked && detected?.Port == SelectedDevice?.Port && detected is not null;
    }
    private void UpdateBackupChoice()
    {
        backupNote.Text = backup.Checked
            ? L.Text("a_full_backup_is_saved_and_verified_before_writing")
            : L.Text("no_backup_will_be_created_you_will_need_an");
        backupNote.ForeColor = backup.Checked ? Muted : Orange;
        if (step == 3) next.Text = backup.Checked ? L.Text("back_up_install") : L.Text("install_569ca4");
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
                deviceStatus.ForeColor = Muted;
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
            deviceStatus.ForeColor = Muted;
            deviceStatus.Text = L.Text("checking_the_connected_device");
            var installer = new Installer(new Progress<string>(s => { if (!IsDisposed) deviceStatus.Text = s; }));
            var result = await installer.Detect(device.Port,SelectedBoard.Id);
            if (!DeviceDiscovery.Find().Contains(device)) throw new IOException(L.Text("the_device_was_unplugged_reconnect_it_to_continue"));
            detected = result; crab.State = "idle"; deviceStatus.ForeColor = Ink;
            deviceStatus.Text = L.Text("device_confirmed", result.Summary, result.DeviceId);
            status.Text = L.Text("usb_connection_checked_no_firmware_has_been_changed");
        } catch (Exception e) {
            crab.State = "error"; deviceStatus.ForeColor = Orange;
            deviceStatus.Text = L.Text("device_check_failed_reconnect_the_usb_cable_or_close");
            status.Text = e.Message.Split('\n')[0];
        } finally { busy = false; content.Enabled = true; UpdateContinue(); }
    }
    private void ReportProgress(string message)
    {
        if (IsDisposed) return;
        status.Text = message;
        if (UsbToolOutput.Percent(message) is { } percent) {
            progress.Style = ProgressBarStyle.Continuous; progress.Value = percent;
        } else progress.Style = ProgressBarStyle.Marquee;
    }
    private void ShowStep()
    {
        var f = Flow(); status.Text = ""; back.Visible = step is > 0 and < 4; next.Enabled = true;
        stepText.Text = string.Join("\n\n", new[] { L.Text("connect_your_display"), L.Text("make_it_yours"), L.Text("companion_settings"), L.Text("review_install") }.Select((s,i) => (i == step ? "→  " : "    ")+s));
        crab.State = step == 4 ? "working" : "idle";
        next.Text = step == 3 ? L.Text("back_up_install") : step == 4 ? L.Text("keep_in_tray") : L.Text("continue");
        if (step == 0) {
            title.Text = L.Text("meet_your_desk_companion"); subtitle.Text = L.Text("connect_your_display_with_a_usb_data_cable_to");
            Add(f,Label(L.Text("pc_language"),10,true),2);Add(f,pcLanguages,12);
            Add(f,Label(L.Text("display_language"),10,true),2);Add(f,displayLanguages,12);
            Add(f,Label(L.Text("setup_languages_note")),18);
            Add(f,Label(L.Text("display_model"),10,true),4); Add(f,boards,8); Add(f,boardDescription,12);
            Add(f,Label(L.Text("connected_usb_display"),10,true),2); Add(f,ports,8);
            Add(f,deviceStatus,8);
            var scan = Button(L.Text("check_again")); scan.Click += async (_,_) => await RefreshDevices(true); Add(f,scan,12);
            Add(f,boardConfirmed,8);
            Add(f,Label(L.Text("usb_confirms_the_chip_and_flash_check_the_exact")),10);
            UpdateContinue();
            var restore = Button(L.Text("restore_a_backup")); restore.Click += async (_,_) => await Restore(); Add(f,restore,12);

        } else if (step == 1) {
            title.Text = L.Text("make_yourself_at_home"); subtitle.Text = L.Text("your_pc_and_clawpal_should_share_the_same_local");
            Add(f,Label(L.Text("wi_fi_name_2_4_ghz"),10,true),2); Add(f,ssid,12);
            Add(f,Label(L.Text("wi_fi_password"),10,true),2); Add(f,password,8);
            var reveal = new ClawSwitch { Text = L.Text("show_password"), Width=600, ForeColor = Muted }; reveal.CheckedChanged += (_,_) => password.UseSystemPasswordChar = !reveal.Checked; Add(f,reveal,15);
            var searchLine = new Panel { Width = 615, Height = 48 }; city.Width = 425; city.PlaceholderText = L.Text("search_a_city_or_village");
            var search = Button(L.Text("find_location")); search.Click += async (_,_) => await Search(search); searchLine.Controls.Add(city); searchLine.Controls.Add(search);searchLine.Resize+=(_,_)=>{city.SetBounds(0,0,Math.Max(80,searchLine.Width-search.Width-12),44);search.Location=new(searchLine.Width-search.Width,0);}; Add(f,searchLine,6);
            Add(f,places,12); Add(f,Label(L.Text("time_zone"),10,true),2); Add(f,zones,0);
        } else if (step == 2) {
            title.Text = L.Text("your_companion_your_rules"); subtitle.Text = L.Text("choose_how_clawpal_behaves_on_your_pc");
            Add(f,startup,16); Add(f,auto,16); Add(f,fps,4);
            Add(f,Label(L.Text("fps_uses_presentmon_to_read_windows_frame_events_it")),25);
            Add(f,Label(L.Text("ready_on_your_display"),14,true),6);
            Add(f,Label(L.Text("home_pc_monitor_clock_weather_spotify_timer_codex")),22);
        } else if (step == 3) {
            title.Text = L.Text("ready_when_you_are"); subtitle.Text = L.Text("review_your_display_settings_and_backup_preference");
            var boardTitle = Label(SelectedBoard.ToString()+" · "+Installer.BundledVersion,16,true); boardTitle.Height=32; Add(f,boardTitle,10);
            foreach (string row in new[] { L.Text("usb") + SelectedDevice?.Port + " · ESP32-S3 · " + (SelectedBoard.FlashBytes/1048576) + " MB", L.Text("wi_fi_e8eec7") + ssid.Text, L.Text("weather_566a6d") + selected, L.Text("time_zone_47a287") + zones.SelectedItem }) {
                var line=Label(row); line.Height=28; Add(f,line,4);
            }
            Add(f,Label(L.Text("language_review",pcLanguages.SelectedItem,displayLanguages.SelectedItem)),6);
            Add(f,Label(L.Text("install_will_replace_the_current_device_firmware_and_its")),10);
            backup.Height=34; Add(f,backup,4); backupNote.Height=48; Add(f,backupNote,6); UpdateBackupChoice();
            var path=Label(L.Text("backups") + Installer.BackupsPath,9); path.Height=38; Add(f,path,2);
            var legal=Label(L.Text("install_license_notice"),9);legal.Height=70;Add(f,legal,10);
            var notices=new ClawButton{Text=L.Text("open_licenses"),Width=260};
            notices.Click+=(_,_)=>{if(preview)return;try{Installer.PreparePayload();OpenFolder(Path.Combine(Installer.PayloadPath,"licenses"));}catch(Exception e){ReportProblem(e.Message);}};Add(f,notices,8);
        } else {
            title.Text = L.Text("a_little_crab_work"); subtitle.Text = L.Text("clawpal_is_preparing_your_display");
            progress.Style = ProgressBarStyle.Marquee; progress.Width = 590; progress.Height = 8; Add(f,progress,30);
            Add(f,Label(backup.Checked ? L.Text("a_backup_first_then_a_fresh_start") : L.Text("a_fresh_start_for_your_display"),17,true),12);
            Add(f,Label(L.Text("keep_this_window_open_and_leave_your_display_connected")),15);
            next.Enabled = false;
        }
    }
    private void LanguageChanged(){if(IsDisposed)return;L.RefreshControls(this);zones.Text=zones.SelectedItem?.ToString()??"";ShowStep();}
    private SetupOptions Options() => new(SelectedDevice?.Port ?? "",ssid.Text,password.Text,selected?.Name ?? "",selected?.Latitude ?? double.NaN,selected?.Longitude ?? double.NaN,(zones.SelectedItem as TimeZoneChoice)?.Posix ?? "",fps.Checked,auto.Checked,startup.Checked,backup.Checked,((LanguageChoice)pcLanguages.SelectedItem!).Code,((LanguageChoice)displayLanguages.SelectedItem!).Code,SelectedBoard.Id);
    private async Task Next()
    {
        try {
            if (step == 4) { if(installed) OpenSettings?.Invoke(); Close(); return; }
            if (busy) return;
            if (step == 0 && (!boardConfirmed.Checked || detected is null || detected.Port != SelectedDevice?.Port)) throw new ArgumentException(L.Text("connect_and_check_your_display_then_confirm_the_board"));
            if (step == 1 || step == 3) Options().Validate();
            if (step < 3) { step++; ShowStep(); return; }
            var options = Options(); step = 4; ShowStep(); busy = true; connection.SetPaused(true);
            var installer = new Installer(new Progress<string>(ReportProgress));
            backupFolder = await installer.Install(options,config,() => { connection.SetAutoOpenApps(config.AutoOpenApps); connection.SetPaused(config.Paused); });
            installed = true; busy = false; password.Clear(); crab.State = "happy"; progress.Style = ProgressBarStyle.Continuous; progress.Value = 100;
            title.Text = L.Text("clawpal_is_installed"); subtitle.Text = L.Text("the_firmware_has_been_written_and_verified");
            var f=Flow();
            if (backupFolder is not null) {
                Add(f,Label(L.Text("your_backup_is_safe"),18,true),15); var folder = Button(L.Text("open_backup_folder")); folder.Width=190; folder.Click += (_,_) => OpenFolder(backupFolder); Add(f,folder,20);
            } else Add(f,Label(L.Text("installed_without_a_backup_as_selected"),16,true),20);
            Add(f,Label(L.Text("clawpal_now_lives_in_your_tray_open_it_any")),16);
            Add(f,Label(L.Text("weather_data_open_meteo_cc_by_4_0"),9),5); var credits=Button(L.Text("about_credits")); credits.Click+=(_,_)=>MessageBox.Show(this,L.Text("clawpal_94dbc7") + typeof(SetupForm).Assembly.GetName().Version?.ToString(3) + L.Text("original_crab_art_supplied_by_the_project_owner_weather"),L.Text("clawpal")); Add(f,credits);
            next.Text=L.Text("open_settings"); next.Enabled = true;
        } catch (Exception e) { busy=false; connection.SetPaused(config.Paused); crab.State="error"; progress.Style=ProgressBarStyle.Continuous; status.Text=e.Message; next.Enabled=true; if(step==4) { title.Text=L.Text("let_s_get_this_sorted"); subtitle.Text=L.Text("setup_did_not_finish_your_saved_backups_remain_available"); var f=Flow(); var error=Label(e.Message); error.Height=160; Add(f,error); var backups=Button(L.Text("open_backups")); backups.Click+=(_,_)=>OpenFolder(Installer.BackupsPath); Add(f,backups); var retry=Button(L.Text("back_to_setup")); retry.Click+=(_,_)=>{step=0;ShowStep();}; Add(f,retry); next.Text=L.Text("close"); } }
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
        ports.Items.Add(new UsbDevice("COM4", L.Text("espressif_usb_device"), "preview")); ports.SelectedIndex=0;
        detected=new("COM4","001122334455",16777216); boardConfirmed.Checked=true;
        deviceStatus.Text=detected.Summary + L.Text("example_device_usb_check_complete_confirm_the_model_below"); deviceStatus.ForeColor=Ink;
        ssid.Text="Home Wi-Fi"; selected=new("Warsaw","Poland","",52.2297,21.0122,"");
        Show(); Application.DoEvents();
        for(int i=0;i<4;i++){ step=i;ShowStep();Application.DoEvents();using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,$"setup-{i}.png"),ImageFormat.Png); }
        backup.Checked=false; ShowStep(); Application.DoEvents();
        using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,"setup-no-backup.png"),ImageFormat.Png);}
        backup.Checked=true; step=4; ShowStep(); ReportProgress(L.Text("backing_up_50_8_0_16_mb_03_00")); Application.DoEvents();
        using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,"setup-backup-progress.png"),ImageFormat.Png);}
        step=4;ShowStep();title.Text=L.Text("let_s_get_this_sorted");subtitle.Text=L.Text("example_error_state_no_device_was_changed");crab.State="error";Application.DoEvents();
        using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,"setup-error.png"),ImageFormat.Png);}
        Close();
    }
    protected override void Dispose(bool disposing) { if(disposing){L.Changed-=LanguageChanged;stateTimer.Dispose();}base.Dispose(disposing); }
}



