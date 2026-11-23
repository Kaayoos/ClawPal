using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ClawPal.Companion;

internal sealed partial class SettingsForm : ClawWindow
{
    /* Rail order: what the display shows, then this PC. About opens from the version line. */
    private enum SettingsPage { Home, Apps, Shortcuts, Printers, Clock, Screen, Look, Pc, Connection, Updates, About }
    private static readonly SettingsPage[][] Groups = [[SettingsPage.Home, SettingsPage.Apps, SettingsPage.Shortcuts, SettingsPage.Printers, SettingsPage.Clock, SettingsPage.Screen, SettingsPage.Look], [SettingsPage.Pc, SettingsPage.Connection, SettingsPage.Updates]];
    private readonly Configuration config;
    private readonly Connection connection;
    private readonly DeviceSettingsClient wireless;
    private DeviceSettings draft;
    private string pcLanguage;
    private bool fps, auto, startup, paused, claudeLive, deviceDirty, pcDirty, saving, shortcutsDirty;
    private ShortcutDeck shortcutDeck;
    private readonly bool preview;
    private readonly ClawScrollPanel pages = new() { Dock = DockStyle.Fill, Padding = new Padding(36, 6, 24, 32) };
    private readonly Panel header = new() { Dock = DockStyle.Top, Height = 132 }, footer = new() { Dock = DockStyle.Bottom, Height = 76, BackColor = ClawTheme.Surface };
    private readonly ClawHeading title = new();
    private readonly Label subtitle = ClawTheme.Label("", 700, ClawTheme.Body, false, 38), status = ClawTheme.Label("", 640, ClawTheme.Body, false, 50);
    private readonly CaseLabel wordmark = new() { Text = L.Text("clawpal"), Font = ClawTheme.Display(20) }, link = new() { Font = ClawTheme.Font(ClawTheme.Small, true), Dot = true };
    private readonly Label version = new() { ForeColor = ClawTheme.CaseInk, Font = ClawTheme.Font(ClawTheme.Small), UseMnemonic = false, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ClawButton save = new() { Text = L.Text("save_changes"), Primary = true, Size = new(176, 46) };
    private readonly ClawButton discord = new() { Text = L.Text("join_discord"), Size = new(196, 42) };
    private readonly CrabView crab = new() { Size = new(96, 96), State = "idle" };
    private readonly Dictionary<SettingsPage, NavButton> navigation = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private Label? updateStatus, releaseStatus, firmwareLabel;
    private ClawButton? installUpdate, prepareUpdate, checkUpdate;
    private Control? activePage;
    private SettingsPage pageIndex;
    private HomePreview? homePreview;
    private HomeOrderList? orderList;
    private ClawWrap? addApps;
    private Label? addNote;
    private string? moment;
    internal event Action? OpenInstaller;
    internal SettingsForm(Configuration config, Connection connection, bool preview = false)
    {
        this.config = config; this.connection = connection; pcLanguage = L.Normalize(config.CompanionLanguage); this.preview = preview; wireless = connection.Settings;
        draft = config.DisplaySettings ?? DeviceSettings.FromConfiguration(config); shortcutDeck = config.Shortcuts ?? ShortcutDeck.Starter(draft.Language); fps = config.TrackFps; auto = config.AutoOpenApps; paused = config.Paused; claudeLive = config.ClaudeLiveUsage; startup = !preview && Startup.Enabled;
        L.SetVariant(draft.Theme);
        Text = L.Text("clawpal_your_desk_companion"); ClientSize = new(1140, 800); AutoScaleMode = AutoScaleMode.Dpi;
        using (var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon")) if (stream is not null) Icon = new Icon(stream);
        // The rail: the name, how the display is doing, the pages, then the community and the version.
        Rail.Controls.AddRange([wordmark, link]);
        int y = NavTop;
        foreach (var group in Groups)
        {
            foreach (var page in group) { var button = new NavButton() { Text = PageName(page), Size = new(RailWidth - 12, NavRow), Location = new(12, y) }; button.Click += (_, _) => ShowPage(page); navigation[page] = button; Rail.Controls.Add(button); y += NavRow; }
            y += GroupGap;
        }
        discord.Click += (_, _) => { if (!preview) Process.Start(new ProcessStartInfo(DiscordInvite) { UseShellExecute = true }); };
        version.Text = L.Text("clawpal_94dbc7") + typeof(SettingsForm).Assembly.GetName().Version?.ToString(3); version.AccessibleName = L.Text("about_clawpal"); version.TabStop = true;
        version.Click += (_, _) => ShowPage(SettingsPage.About);
        version.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { ShowPage(SettingsPage.About); e.Handled = true; } };
        var aboutHint = new ToolTip(); aboutHint.SetToolTip(version, L.Text("about_clawpal")); Disposed += (_, _) => aboutHint.Dispose();
        Rail.Controls.AddRange([discord, version]); Rail.Resize += (_, _) => LayoutRail(); LayoutRail();
        // The screen: the page's shout and its crab, the page, then the strip that saves.
        title.Text = " "; header.Controls.AddRange([title, subtitle, crab]); header.Resize += (_, _) => LayoutHeader(); title.TextChanged += (_, _) => LayoutHeader(); subtitle.TextChanged += (_, _) => LayoutHeader();
        footer.Controls.AddRange([status, save]); footer.Resize += (_, _) => LayoutFooter();
        ScreenArea.Controls.Add(pages); ScreenArea.Controls.Add(header); ScreenArea.Controls.Add(footer);
        save.Click += async (_, _) => await Save();
        refresh.Tick += (_, _) => RefreshState(); refresh.Start();
        momentTimer.Tick += (_, _) => { momentTimer.Stop(); if (moment is "happy") SetMoment(null); };
        Shown += async (_, _) => { if (!preview) { await LoadDisplay(); await RefreshFirmware(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing && !preview) { e.Cancel = true; Hide(); } };
        L.Changed += LanguageChanged; ShowPage(SettingsPage.Home); RefreshState();
    }
    private const string DiscordInvite = "https://discord.com/invite/gR5DqCdGre";
    internal const int NavTop = 116, NavRow = 42, GroupGap = 18;
    private static string PageName(SettingsPage page) => page switch
    {
        SettingsPage.Home => L.Text("home"), SettingsPage.Apps => L.Text("apps"), SettingsPage.Shortcuts => L.Text("shortcuts"), SettingsPage.Printers => L.Text("printers"), SettingsPage.Clock => L.Text("clock_weather"),
        SettingsPage.Screen => L.Text("screen"), SettingsPage.Look => L.Text("look_language"), SettingsPage.Pc => L.Text("pc_app_nav"), SettingsPage.Connection => L.Text("connection_nav"),
        SettingsPage.Updates => L.Text("updates"), _ => L.Text("about_clawpal")
    };
    /* Each page has a crab doing its thing; saving and problems take over for a moment. */
    private static string Mood(SettingsPage page) => page switch
    {
        SettingsPage.Apps => "music", SettingsPage.Shortcuts => "typing", SettingsPage.Printers => "working", SettingsPage.Clock => "sunny", SettingsPage.Screen => "night", SettingsPage.Look => "happy",
        SettingsPage.Pc => "working", SettingsPage.Connection => "thinking", SettingsPage.Updates => "carrying", _ => "idle"
    };
    private void SetMoment(string? state)
    {
        moment = state; crab.State = state ?? Mood(pageIndex);
        momentTimer.Stop(); if (state is "happy") momentTimer.Start();
    }
    private readonly System.Windows.Forms.Timer momentTimer = new() { Interval = 3500 };
    private void LayoutRail()
    {
        int pad = LogicalToDeviceUnits(24), w = Rail.ClientSize.Width - 2 * pad;
        wordmark.SetBounds(pad - 2, LogicalToDeviceUnits(18), w, LogicalToDeviceUnits(44));
        link.SetBounds(pad, LogicalToDeviceUnits(62), w, LogicalToDeviceUnits(40));
        version.SetBounds(pad, Rail.ClientSize.Height - LogicalToDeviceUnits(42), w, LogicalToDeviceUnits(24));
        discord.SetBounds(pad - 4, version.Top - LogicalToDeviceUnits(52), w + 8, LogicalToDeviceUnits(42));
    }
    private void LayoutHeader()
    {
        int left = LogicalToDeviceUnits(36), right = LogicalToDeviceUnits(28), size = LogicalToDeviceUnits(96), top = LogicalToDeviceUnits(30);
        crab.SetBounds(header.ClientSize.Width - right - size, LogicalToDeviceUnits(18), size, size);
        int width = Math.Max(120, crab.Left - left - LogicalToDeviceUnits(20));
        title.SetBounds(left, top, width, title.Measure(width));
        subtitle.SetBounds(left + 1, title.Bottom + LogicalToDeviceUnits(8), width, ClawStack.TextHeight(subtitle, width));
        int height = Math.Max(crab.Bottom, subtitle.Bottom) + LogicalToDeviceUnits(22);
        if (header.Height != height) header.Height = height;
    }
    private void LayoutFooter()
    {
        int pad = LogicalToDeviceUnits(28);
        save.Location = new(footer.ClientSize.Width - save.Width - pad, (footer.ClientSize.Height - save.Height) / 2);
        status.SetBounds(LogicalToDeviceUnits(36), 0, Math.Max(180, save.Left - LogicalToDeviceUnits(60)), footer.ClientSize.Height);
    }
    private void RefreshState()
    {
        bool online = preview || connection.IsConnected;
        var updates = connection.Updates;
        if (updateStatus is not null) updateStatus.Text = preview ? L.Text("ready_for_wi_fi_updates_your_settings_stay_on") : updates.Status;
        if (releaseStatus is not null) releaseStatus.Text = preview ? L.Text("official_update_source") : updates.ReleaseStatus;
        if (firmwareLabel is not null) firmwareLabel.Text = L.Text("versions", preview ? Installer.BundledVersion : updates.Device?.Version ?? L.Text("not_connected"), typeof(SettingsForm).Assembly.GetName().Version?.ToString(3));
        if (installUpdate is not null) { installUpdate.Visible = updates.Device?.OtaReady != false; installUpdate.Enabled = !updates.Busy && !updates.Checking && updates.Device?.OtaReady == true && !updates.CompanionRequired && (updates.AvailableVersion is not null || updates.BundledNewer); installUpdate.Text = updates.AvailableVersion is { } v ? L.Text("install_version", v) : L.Text("install_bundled_update"); }
        if (prepareUpdate is not null) { prepareUpdate.Visible = updates.Device?.OtaReady == false; prepareUpdate.Enabled = !updates.Busy; }
        if (checkUpdate is not null) checkUpdate.Enabled = !updates.Busy && !updates.Checking;
        var updatesName = updates.NotificationVersion is null ? L.Text("updates") : L.Text("updates_1"); if (navigation[SettingsPage.Updates].Text != updatesName) navigation[SettingsPage.Updates].Text = updatesName;
        if (updates.Busy) crab.State = "working";
        pages.Enabled = !updates.Busy;
        string linkText = updates.Busy ? L.Text("updating_your_display") : config.Paused ? L.Text("pc_monitoring_paused") : online ? L.Text("connected_over_wi_fi") : L.Text("display_offline_settings_are_still_available");
        if (link.Text != linkText) link.Text = linkText; if (link.Lit != online) { link.Lit = online; link.Invalidate(); }
        save.Enabled = !saving && !updates.Busy && (deviceDirty || pcDirty);
        if (updates.Busy) { status.Text = updates.Status; return; }
        if (!saving && !deviceDirty && !pcDirty) status.Text = preview ? L.Text("preview_changes_shown_here_are_illustrative") : wireless.NeedsFirmwareUpdate ? L.Text("open_updates_to_install_the_firmware_for_these_settings") : config.SettingsPending ? wireless.Status : config.Paused ? L.Text("pc_monitoring_is_paused_display_settings_can_still_sync") : online ? L.Text("all_changes_saved_make_yourself_at_home") : L.Text("display_offline_saved_changes_will_sync_when_it_returns");
    }
    private void Dirty(bool device)
    {
        if (device) deviceDirty = true; else pcDirty = true; status.Text = L.Text("you_have_unsaved_changes"); save.Enabled = !saving; if (moment is "happy" or "error") SetMoment(null);
    }
    private void SetDraft(DeviceSettings value)
    {
        bool look = value.Theme != draft.Theme;
        draft = value; Dirty(true); homePreview?.SetSettings(draft); orderList?.SetSettings(draft); RefreshAddApps();
        // Words about the display follow its look; the crab here stays either way.
        if (look) BeginInvoke(() => L.SetVariant(draft.Theme));
    }
    private void Clear()
    {
        homePreview = null; orderList = null; addApps = null; addNote = null; updateStatus = null; releaseStatus = null; firmwareLabel = null; installUpdate = null; prepareUpdate = null; checkUpdate = null;
        foreach (Control child in pages.Content.Controls.Cast<Control>().ToArray()) child.Dispose();
    }
    /* A page whose parts arrange themselves by hand (Home and Shortcuts, which place a preview beside an editor). */
    private Panel Page(int height = 510)
    {
        Clear(); var p = new Panel() { Dock = DockStyle.Top, Width = pages.Content.Width, Height = height, BackColor = ClawTheme.Screen };
        pages.Content.Controls.Add(p); activePage = p; pages.ResetScroll(); return p;
    }
    /* Every other page: its parts one above the other, with room between them. */
    private ClawStack Stack()
    {
        Clear(); var p = new ClawStack() { Dock = DockStyle.Top, Width = pages.Content.Width, Gap = 44, BackColor = ClawTheme.Screen, Padding = new Padding(0, 4, 0, 12) };
        pages.Content.Controls.Add(p); activePage = p; pages.ResetScroll(); return p;
    }
    private static Label Note(string text) => ClawTheme.Label(text, 400, ClawTheme.Small, false, 20);
    private static Label Caption(string text) => ClawTheme.Label(text, 400, ClawTheme.Body, true, 24);
    private static ClawSwitch Switch(string text, bool value, Action<bool> change)
    {
        var control = new ClawSwitch() { Text = text, Checked = value }; control.CheckedChanged += (_, _) => change(control.Checked); return control;
    }
    private static ClawRow Row(int stackBelow = 0, params (Control control, int width)[] items)
    {
        var row = new ClawRow() { StackBelow = stackBelow }; foreach (var (control, width) in items) row.Add(control, width); return row;
    }
    private static ClawStack Column(params Control[] items) { var column = new ClawStack() { Gap = 8 }; foreach (var item in items) column.Add(item); return column; }
    private void ShowPage(SettingsPage index)
    {
        pageIndex = index; foreach (var (page, button) in navigation) { bool was = button.Selected; button.Selected = page == index; if (button.Selected && !was) button.Grow(); button.Invalidate(); }
        moment = null; crab.State = connection.Updates.Busy ? "working" : Mood(index);
        switch (index)
        {
            case SettingsPage.Home: HomePage(); break; case SettingsPage.Apps: AppsPage(); break; case SettingsPage.Shortcuts: ShortcutsPage(); break; case SettingsPage.Printers: PrintersPage(); break;
            case SettingsPage.Clock: ClockPage(); break; case SettingsPage.Screen: ScreenPage(); break; case SettingsPage.Look: LookPage(); break;
            case SettingsPage.Pc: PcPage(); break; case SettingsPage.Connection: ConnectionPage(); break; case SettingsPage.Updates: UpdatesPage(); break; default: AboutPage(); break;
        }
        LayoutHeader(); pages.Arrive();
    }

    private void LanguageChanged() { if (IsDisposed) return; pcLanguage = L.Language; L.RefreshControls(this); foreach (var (page, button) in navigation) button.Text = PageName(page); ShowPage(pageIndex); RefreshState(); }
    private void AboutPage()
    {
        title.Text = L.Text("about_clawpal"); subtitle.Text = L.Text("created_by_minimalpulse"); var p = Stack();
        ClawButton Link(string caption, string url) { var b = new ClawButton { Text = caption }; b.Click += (_, _) => { if (!preview) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }; return b; }
        // The name at the size of a sign, with the version beside it.
        var mark = new AboutMark(L.Text("clawpal"), typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "") { Height = 104 };
        var about = p.Add(new ClawSection());
        about.Add(mark, 18);
        about.Add(Row(520, (Link("minimalpulse.com", "https://minimalpulse.com/"), 0), (Link("github.com/Kaayoos/clawpal", "https://github.com/Kaayoos/clawpal"), 0)), 16);
        about.Add(ClawTheme.Label(L.Text("about_license"), 400, ClawTheme.Body, false, 22));
        var credits = p.Add(new ClawSection(L.Text("about_credits_title")));
        credits.Add(ClawTheme.Label(L.Text("about_weather"), 400, ClawTheme.Body, false, 20)); credits.Add(ClawTheme.Label(L.Text("about_tools"), 400, ClawTheme.Body, false, 20)); credits.Add(Note(L.Text("about_marks")), 16);
        var licenses = new ClawButton { Text = L.Text("open_licenses") };
        licenses.Click += (_, _) => { if (preview) return; Installer.PreparePayload(); Process.Start(new ProcessStartInfo(Path.Combine(Installer.PayloadPath, "licenses", "THIRD_PARTY_NOTICES.txt")) { UseShellExecute = true }); };
        credits.Add(Row(0, (licenses, 230)));
        p.Add(Note(L.Text("about_device_hint")));
    }

    /* The display's look and language. This app keeps its own look whatever the display wears. */
    private void LookPage()
    {
        title.Text = L.Text("look_language_title"); subtitle.Text = L.Text("look_language_description"); var p = Stack();
        var lookCard = p.Add(new ClawSection(L.Text("look_section"), L.Text("look_app_note")));
        var picker = new LookPicker(draft.Theme, draft.Accent); lookCard.Add(picker);
        picker.Changed += (theme, accent) => SetDraft(draft with { Theme = theme, Accent = accent });
        lookCard.Add(Note(L.Text("appearance_restart_note")));
        var languageCard = p.Add(new ClawSection(L.Text("language"), L.Text("language_note")));
        ClawStack Picker(string caption, string code, Action<string> change)
        {
            var box = new ChoiceBox(); foreach (var language in L.Languages) box.Items.Add(language);
            box.SelectedIndex = L.Languages.ToList().FindIndex(l => l.Code == code);
            box.SelectedIndexChanged += (_, _) => { if (box.SelectedItem is LanguageChoice language) change(language.Code); };
            return Column(Caption(caption), box);
        }
        languageCard.Add(Row(560, (Picker(L.Text("pc_language"), pcLanguage, code => { pcLanguage = code; Dirty(false); }), 0), (Picker(L.Text("display_language"), draft.Language, code => SetDraft(draft with { Language = code })), 0)));
    }

    private void HomePage()
    {
        title.Text = L.Text("a_place_for_your_favorites"); subtitle.Text = L.Text("arrange_your_shortcuts_your_crab_keeps_the_best_seat");
        var p = Page(); homePreview = new(draft, config.BoardId); orderList = new(draft) { Font = ClawTheme.Font() }; p.Controls.AddRange([homePreview, orderList]);
        var board = BoardCatalog.Get(config.BoardId);
        var previewCaption = ClawTheme.Label(L.Text("your_display_dimensions", board.Width, board.Height), 390, ClawTheme.Small, false, 24);
        var listCaption = ClawTheme.Label(L.Text("on_your_home"), 300, ClawTheme.Head, true, 30);
        var instruction = ClawTheme.Label(L.Text("drag_to_reorder_the_minus_removes_a_shortcut"), 330, ClawTheme.Small, false, 38);
        var hint = ClawTheme.Label(L.Text("apps_stay_available_in_the_display_s_apps_menu"), 395, ClawTheme.Small, false, 65);
        var addCaption = ClawTheme.Label(L.Text("add_to_home"), 300, ClawTheme.Head, true, 30);
        addNote = ClawTheme.Label("", 330, ClawTheme.Small, false, 38); addApps = new ClawWrap() { ItemWidth = 150, ItemHeight = 50, Gap = 8 };
        // Home's footer is quiet unless the version is asked for.
        var version = Switch(L.Text("show_the_clawpal_version_on_home"), draft.ShowVersion, value => SetDraft(draft with { ShowVersion = value }));
        p.Controls.AddRange([previewCaption, listCaption, instruction, hint, version, addCaption, addNote, addApps]);
        orderList.Changed += SetDraft;
        void Layout()
        {
            if (homePreview is null || orderList is null || addApps is null || addNote is null) return;
            bool sideBySide = p.Width >= 760;
            int left = sideBySide ? Math.Min(620, (int)(p.Width * .56)) : Math.Min(560, p.Width);
            float ratio = board.Height / (float)board.Width;
            int previewHeight = DeviceArt.HeightFor(left, ratio);
            previewCaption.SetBounds(0, 0, left, 24); homePreview.SetBounds(0, 30, left, previewHeight);
            hint.SetBounds(0, homePreview.Bottom + 18, left, ClawStack.TextHeight(hint, left)); version.SetBounds(0, hint.Bottom + 10, left, 44);
            int x = sideBySide ? left + 36 : 0, y = sideBySide ? 0 : version.Bottom + 32, right = sideBySide ? p.Width - x : left;
            listCaption.SetBounds(x, y - 4, right, 30); instruction.SetBounds(x, y + 28, right, ClawStack.TextHeight(instruction, right));
            int listHeight = Math.Max(62, draft.HomeOrder.Length * 62); orderList.SetBounds(x, instruction.Bottom + 12, right, listHeight);
            int addTop = orderList.Bottom + 28; addCaption.SetBounds(x, addTop, right, 30);
            addNote.SetBounds(x, addTop + 32, right, ClawStack.TextHeight(addNote, right));
            addApps.SetBounds(x, addNote.Bottom + 10, right, addApps.Height); addApps.PerformLayout();
            p.Height = Math.Max(version.Bottom, addApps.Bottom) + 24;
        }
        addApps.Resize += (_, _) => Layout(); p.Resize += (_, _) => Layout(); orderList.Changed += _ => Layout(); RefreshAddApps(); Layout();
    }
    /* Every enabled app not yet on Home, as a tile that adds it. */
    private void RefreshAddApps()
    {
        if (addApps is null || addNote is null) return;
        foreach (Control chip in addApps.Controls.Cast<Control>().ToArray()) chip.Dispose();
        bool full = draft.HomeOrder.Length >= 5;
        var waiting = DisplayApps.All.Where(a => draft.Enabled(a.Id) && !draft.HomeOrder.Contains(a.Id)).ToArray();
        addNote.Text = waiting.Length == 0 ? L.Text("all_enabled_apps_are_on_home") : full ? L.Text("home_is_full_remove_one") : L.Text("click_an_app_to_add_it");
        foreach (var app in waiting)
        {
            var chip = new AppChip(app) { Enabled = !full };
            chip.Click += (_, _) => { if (draft.HomeOrder.Length < 5 && !draft.HomeOrder.Contains(app.Id)) SetDraft(draft with { HomeOrder = draft.HomeOrder.Append(app.Id).ToArray() }); };
            addApps.Controls.Add(chip);
        }
        addApps.PerformLayout();
    }
    /* Every app as a line of its own, large enough to read across the room, with its switch and what belongs to it. */
    private void AppsPage()
    {
        title.Text = L.Text("a_little_less_or_all_of_it"); subtitle.Text = L.Text("choose_the_apps_available_on_your_display_home_stays");
        var p = Stack(); p.Gap = 34;
        var general = p.Add(new ClawCard(L.Text("auto_open_title")));
        general.Add(Switch(L.Text("automatically_open_spotify_or_codex"), auto, value => { auto = value; Dirty(false); }), 2);
        general.Add(Note(L.Text("when_an_enabled_app_opens_on_your_pc_show")));
        foreach (var app in DisplayApps.All)
        {
            var item = p.Add(new ClawStack() { Gap = 10 });
            var header = new AppHeader(app, draft.Enabled(app.Id)); header.Toggled += on => SetDraft(draft.SetEnabled(app.Id, on)); item.Add(header);
            var options = new ClawStack() { Gap = 10, Padding = new Padding(AppHeader.TextLeft, 0, 0, 0) };
            if (app.Id == 0)
            {
                options.Add(Switch(L.Text("track_game_fps_in_pc_monitor"), fps, value => { fps = value; Dirty(false); }), 2);
                options.Add(Note(L.Text("uses_windows_frame_events_through_presentmon_game_files_stay")));
            }
            else if (app.Id == 7) ClaudeOptions(options);
            else if (app.Id == 9) options.Add(Note(L.Text("audio_app_note")));
            else if (app.Id == 10) { var edit = new ClawButton { Text = L.Text("manage_printers") }; edit.Click += (_, _) => ShowPage(SettingsPage.Printers); options.Add(Row(0, (edit, 230))); }
            else if (app.Id == 8) { var edit = new ClawButton { Text = L.Text("edit_shortcut_tiles") }; edit.Click += (_, _) => ShowPage(SettingsPage.Shortcuts); options.Add(Row(0, (edit, 230))); }
            else if (app.Id is 1 or 6) { var edit = new ClawButton { Text = L.Text("clock_weather_settings") }; edit.Click += (_, _) => ShowPage(SettingsPage.Clock); options.Add(Row(0, (edit, 260))); }
            if (options.Controls.Count > 0) item.Add(options);
            else options.Dispose();
        }
        p.Add(Note(L.Text("turning_an_app_off_also_removes_its_home_shortcut")));
    }
    private void ClaudeOptions(ClawStack card)
    {
        card.Add(Caption(L.Text("claude_link_title")), 4);
        card.Add(Note(L.Text("claude_link_description")));
        var claudeLink = new ClawButton() { Text = L.Text("claude_link_button") };
        var claudeUnlink = new ClawButton() { Text = L.Text("claude_link_disconnect") };
        card.Add(Row(520, (claudeLink, 0), (claudeUnlink, 0)));
        var claudeStatus = card.Add(Note(preview ? L.Text("claude_link_ready") : ClaudeBridge.Status), 16);
        var liveSwitch = card.Add(Switch(L.Text("claude_live_limits"), claudeLive, value => { claudeLive = value; Dirty(false); }), 2);
        card.Add(Note(L.Text("claude_live_description")));
        claudeLink.Click += (_, _) => { try { if (!preview) ClaudeBridge.Install(); claudeStatus.Text = L.Text("claude_link_ready"); SetMoment("happy"); } catch (Exception error) { claudeStatus.Text = error.Message; SetMoment("error"); } };
        claudeUnlink.Click += (_, _) => { try { if (!preview) { ClaudeBridge.Uninstall(); config.ClaudeLiveUsage = false; config.Save(); } liveSwitch.Checked = false; claudeLive = false; claudeStatus.Text = L.Text("claude_link_removed"); SetMoment(null); } catch (Exception error) { claudeStatus.Text = error.Message; SetMoment("error"); } };
    }
    private sealed record DeckSize(int Tiles) { public override string ToString() => L.Text("n_tiles", Tiles); }
    /* The deck as the display shows it, beside everything about the chosen tile. */
    private void ShortcutsPage()
    {
        title.Text = L.Text("your_pc_at_a_tap"); subtitle.Text = L.Text("tiles_on_your_display_that_run_things_on_this_pc");
        var p = Page(640);
        var sizeCaption = ClawTheme.Label(L.Text("grid"), 90, ClawTheme.Body, true, 44); sizeCaption.TextAlign = ContentAlignment.MiddleLeft;
        var size = new ChoiceBox() { Width = 170, Height = 44 }; foreach (int n in ShortcutDeck.Sizes) size.Items.Add(new DeckSize(n));
        size.SelectedIndex = Array.IndexOf(ShortcutDeck.Sizes, shortcutDeck.Size);
        var restore = new ClawButton() { Text = L.Text("restore_starter_shortcuts"), Size = new(220, 44) };
        var deckPreview = new DeckPreview(shortcutDeck, config.BoardId, () => draft.Language, () => Themes.Get(draft.Theme, draft.Accent));
        var hint = ClawTheme.Label(L.Text("click_a_tile_to_edit_it_drag_to_move_it"), 400, ClawTheme.Small, false, 44);
        var state = ClawTheme.Label("", 400, ClawTheme.Small, false, 44);
        p.Controls.AddRange([sizeCaption, hint, state]);
        ShortcutInspector? inspector = null;
        void Change(ShortcutDeck deck) { shortcutDeck = deck; shortcutsDirty = true; Dirty(false); deckPreview.SetDeck(deck); ShowState(); }
        // Says whether the deck can be saved, and if not, why.
        void ShowState() { try { shortcutDeck.Validate(); state.Text = draft.Enabled(8) ? "" : L.Text("shortcuts_are_off_in_apps"); state.ForeColor = ClawTheme.Muted; } catch (ArgumentException e) { state.Text = e.Message; state.ForeColor = ClawTheme.Warning; } }
        inspector = new ShortcutInspector(() => shortcutDeck, () => draft.Language, Change);
        deckPreview.Changed += deck => { Change(deck); inspector.Bind(deckPreview.Selected); };
        deckPreview.SelectedChanged += slot => inspector.Bind(slot);
        size.SelectedIndexChanged += (_, _) =>
        {
            if (size.SelectedItem is not DeckSize choice || choice.Tiles == shortcutDeck.Size) return;
            try { Change(shortcutDeck.Resize(choice.Tiles)); deckPreview.Select(Math.Min(deckPreview.Selected, choice.Tiles - 1)); Layout(); }
            catch (ArgumentException e) { state.Text = e.Message; state.ForeColor = ClawTheme.Warning; BeginInvoke(() => size.SelectedIndex = Array.IndexOf(ShortcutDeck.Sizes, shortcutDeck.Size)); }
        };
        restore.Click += (_, _) => { Change(ShortcutDeck.Starter(draft.Language)); size.SelectedIndex = Array.IndexOf(ShortcutDeck.Sizes, shortcutDeck.Size); deckPreview.Select(0); };
        p.Controls.AddRange([size, restore, deckPreview, inspector]);
        void Layout()
        {
            bool sideBySide = p.Width >= 790;
            int left = sideBySide ? Math.Min(600, (int)(p.Width * .58)) : Math.Min(600, p.Width);
            var model = BoardCatalog.Get(config.BoardId); bool tiny = model.Height < 400;
            int columns = shortcutDeck.Size == 6 || (tiny && shortcutDeck.Size == 12) ? 3 : 4, rows = (shortcutDeck.Size + columns - 1) / columns;
            float aspect = tiny ? ((model.Height - 44) * ((rows + 1) / 2) + 44) / (float)model.Width : model.Height / (float)model.Width;
            int previewHeight = DeviceArt.HeightFor(left, aspect);
            sizeCaption.SetBounds(0, 0, 70, 44); size.SetBounds(74, 0, 170, 44); restore.SetBounds(Math.Max(254, left - 220), 0, 220, 44);
            deckPreview.SetBounds(0, 62, left, previewHeight); hint.SetBounds(0, deckPreview.Bottom + 14, left, ClawStack.TextHeight(hint, left)); state.SetBounds(0, hint.Bottom, left, 44);
            int x = sideBySide ? left + 36 : 0, y = sideBySide ? 0 : state.Bottom + 16;
            inspector.SetBounds(x, y, sideBySide ? p.Width - x : left, inspector.Height);
            p.Height = Math.Max(state.Bottom, inspector.Bottom) + 24;
        }
        inspector.Resize += (_, _) => Layout(); p.Resize += (_, _) => Layout(); Layout();
        deckPreview.Select(Math.Clamp(deckPreview.Selected, 0, shortcutDeck.Size - 1)); ShowState();
    }
    private void ClockPage()
    {
        title.Text = L.Text("your_time_your_little_forecast"); subtitle.Text = L.Text("time_and_weather_are_saved_on_the_display_and");
        var p = Stack();
        // Each format shows itself: the choice is the time as the display will write it.
        var clock = p.Add(new ClawSection(L.Text("time_format")));
        var now = DateTime.Now;
        var twentyFour = new BigChoice(now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), L.Text("time_format_24")) { Selected = draft.Use24h, AccessibleName = L.Text("24_hours_17_34") };
        var twelve = new BigChoice(now.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), L.Text("time_format_12")) { Selected = !draft.Use24h, AccessibleName = L.Text("12_hours_5_34_pm") };
        clock.Add(Row(0, (twentyFour, 0), (twelve, 0)));
        void Hours(bool value) { SetDraft(draft with { Use24h = value }); twentyFour.Selected = value; twelve.Selected = !value; twentyFour.Invalidate(); twelve.Invalidate(); }
        twentyFour.Click += (_, _) => Hours(true); twelve.Click += (_, _) => Hours(false);

        var weather = p.Add(new ClawSection(L.Text("weather_card")));
        weather.Add(Switch(L.Text("show_minimal_weather_in_clock"), draft.WeatherEnabled, value => SetDraft(draft with { WeatherEnabled = value })), 18);
        weather.Add(Caption(L.Text("weather_location")), 6);
        var city = ClawTheme.Input(L.Text("find_a_city_or_village")); city.Text = draft.City == "Weather" ? "" : draft.City;
        var search = new ClawButton() { Text = L.Text("find_location") };
        weather.Add(Row(0, (city, 0), (search, 170)), 8);
        var choices = new ChoiceBox();
        choices.Items.Add(new Place(draft.City, "", "", draft.Latitude, draft.Longitude, "")); choices.SelectedIndex = 0; weather.Add(choices, 6);
        var feedback = weather.Add(Note(L.Text("search_then_choose_the_location_that_matches")), 22);
        weather.Add(Caption(L.Text("weather_temperature_unit")), 8);
        var celsius = new BigChoice("°C", L.Text("celsius")) { Selected = !draft.WeatherFahrenheit };
        var fahrenheit = new BigChoice("°F", L.Text("fahrenheit")) { Selected = draft.WeatherFahrenheit };
        weather.Add(Row(0, (celsius, 0), (fahrenheit, 0)), 8);
        void Units(bool useFahrenheit) { SetDraft(draft with { WeatherFahrenheit = useFahrenheit }); celsius.Selected = !useFahrenheit; fahrenheit.Selected = useFahrenheit; celsius.Invalidate(); fahrenheit.Invalidate(); }
        celsius.Click += (_, _) => Units(false); fahrenheit.Click += (_, _) => Units(true);
        weather.Add(Note(L.Text("weather_unit_scope")));

        var zoneCard = p.Add(new ClawSection(L.Text("time_zone")));
        var zones = new ChoiceBox(); TimeZones.Fill(zones, draft.Timezone); zoneCard.Add(zones);
        zones.SelectedIndexChanged += (_, _) => { if (zones.SelectedItem is TimeZoneChoice zone) SetDraft(draft with { Timezone = zone.Posix }); };
        choices.SelectedIndexChanged += (_, _) => { if (choices.SelectedItem is Place place) { SetDraft(draft with { City = place.Name, Latitude = place.Latitude, Longitude = place.Longitude }); TimeZones.SelectFor(zones, place); } };
        search.Click += async (_, _) =>
        {
            if (city.Text.Trim().Length < 2) { feedback.Text = L.Text("type_at_least_two_letters"); return; }
            search.Enabled = false; feedback.Text = L.Text("looking_for_your_corner_of_the_world"); SetMoment("thinking");
            try
            {
                using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(12) };
                using var json = JsonDocument.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=8&language=" + Uri.EscapeDataString(L.Language) + "&name=" + Uri.EscapeDataString(city.Text.Trim())));
                if (IsDisposed || choices.IsDisposed) return;
                choices.Items.Clear(); choices.SelectedIndex = -1;
                if (json.RootElement.TryGetProperty("results", out var places)) foreach (var place in places.EnumerateArray())
                    {
                        string Str(string key) => place.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
                        choices.Items.Add(new Place(Str("name"), Str("country"), Str("admin1"), place.GetProperty("latitude").GetDouble(), place.GetProperty("longitude").GetDouble(), Str("timezone")));
                    }
                if (choices.Items.Count > 0) choices.SelectedIndex = 0;
                feedback.Text = choices.Items.Count > 0 ? L.Text("choose_your_location_from_the_results") : L.Text("no_match_try_a_nearby_town"); SetMoment(choices.Items.Count > 0 ? null : "error");
            }
            catch (Exception) { if (!feedback.IsDisposed) feedback.Text = L.Text("location_search_is_unavailable_check_your_internet_connection_and"); SetMoment("error"); }
            finally { if (!search.IsDisposed) search.Enabled = true; }
        };
        p.Add(Note(L.Text("weather_by_open_meteo_location_search_by_geonames")));
    }
    private sealed record SettingChoice(int Value, string Label) { public override string ToString() => Label; }
    private void ScreenPage()
    {
        title.Text = L.Text("a_screen_that_settles_with_you"); subtitle.Text = L.Text("touch_to_wake_these_preferences_work_on_every_app");
        var p = Stack();
        var dim = p.Add(new ClawSection(L.Text("automatic_dimming")));
        var modes = new[] { (1, L.Text("pc_offline")), (2, L.Text("whenever_idle")), (0, L.Text("never")) };
        var modeButtons = modes.Select(mode => new ClawButton { Text = mode.Item2, Selected = draft.DimBehavior == mode.Item1, AccessibleName = L.Text("dim") + mode.Item2, Height = 48 }).ToArray();
        dim.Add(Row(0, modeButtons.Select(b => ((Control)b, 0)).ToArray()), 10);
        var behaviorNote = dim.Add(Note(""), 22);
        var delay = new ClawTimeInput { Minimum = 15, Maximum = 3600, Step = 15, Format = ClawNumberFormat.Duration, Value = draft.DimAfterSeconds, AccessibleName = L.Text("idle_delay") };
        var brightness = new ClawSlider { Value = draft.DimBrightness };
        dim.Add(Row(560, (Column(Caption(L.Text("idle_delay_minutes_seconds")), delay, Note(L.Text("15_seconds_to_60_minutes"))), 0), (Column(Caption(L.Text("dimmed_brightness")), brightness, Note(L.Text("touch_restores_full_brightness"))), 0)));
        delay.ValueChanged += (_, _) => SetDraft(draft with { DimAfterSeconds = delay.Value }); brightness.ValueChanged += (_, _) => SetDraft(draft with { DimBrightness = brightness.Value });
        void Mode(int value)
        {
            for (int i = 0; i < modes.Length; i++) { modeButtons[i].Selected = modes[i].Item1 == value; modeButtons[i].Invalidate(); }
            delay.Enabled = brightness.Enabled = value != 0;
            behaviorNote.Text = value switch { 1 => L.Text("dim_after_inactivity_when_your_pc_is_disconnected"), 2 => L.Text("dim_after_inactivity_even_while_your_pc_is_connected"), _ => L.Text("keep_normal_brightness_night_hours_can_still_turn_the") };
        }
        for (int i = 0; i < modes.Length; i++) { int value = modes[i].Item1; modeButtons[i].Click += (_, _) => { SetDraft(draft with { DimBehavior = value }); Mode(value); }; }
        Mode(draft.DimBehavior);

        var night = p.Add(new ClawSection(L.Text("night_hours_card")));
        var start = new ClawTimeInput { Value = draft.NightStart, AccessibleName = L.Text("night_hours_start") }; var end = new ClawTimeInput { Value = draft.NightEnd, AccessibleName = L.Text("night_hours_end") };
        night.Add(Switch(L.Text("turn_the_screen_off_during_night_hours"), draft.NightEnabled, value => { SetDraft(draft with { NightEnabled = value }); start.Enabled = end.Enabled = value; }), 14);
        night.Add(Row(560, (Column(Caption(L.Text("from_24_hour_time")), start), 0), (Column(Caption(L.Text("until_24_hour_time")), end), 0)));
        start.Enabled = end.Enabled = draft.NightEnabled;
        start.ValueChanged += (_, _) => SetDraft(draft with { NightStart = start.Value }); end.ValueChanged += (_, _) => SetDraft(draft with { NightEnd = end.Value });
        night.Add(Note(L.Text("uses_the_display_s_local_time_while_your_pc")));

        var offlineCard = p.Add(new ClawSection(L.Text("when_the_pc_has_been_offline_for_30_seconds")));
        var apps = new[] { new SettingChoice(5, L.Text("home")) }.Concat(DisplayApps.All.Where(a => draft.Enabled(a.Id) && a.Id is not 8 and not 9).Select(a => new SettingChoice(a.Id, a.Name)));
        var offline = new ChoiceBox(); foreach (var choice in apps) offline.Items.Add(choice);
        offline.SelectedIndex = offline.Items.Cast<SettingChoice>().ToList().FindIndex(c => c.Value == (draft.Enabled(draft.OfflineApp) ? draft.OfflineApp : 5));
        offline.SelectedIndexChanged += (_, _) => { if (offline.SelectedItem is SettingChoice c) SetDraft(draft with { OfflineApp = c.Value }); };
        offlineCard.Add(offline); offlineCard.Add(Note(L.Text("clock_is_the_default_a_running_timer_delays_automatic")));
    }
    private void PcPage()
    {
        title.Text = L.Text("a_quiet_companion_on_your_pc"); subtitle.Text = L.Text("these_settings_apply_here_no_usb_cable_or_display");
        var p = Stack();
        var startCard = p.Add(new ClawSection(L.Text("startup_card")));
        startCard.Add(Switch(L.Text("start_clawpal_with_windows"), startup, value => { startup = value; Dirty(false); }), 2);
        startCard.Add(Note(L.Text("starts_quietly_in_the_tray_open_this_window_whenever")));
        var monitorCard = p.Add(new ClawSection(L.Text("pc_monitoring_card")));
        monitorCard.Add(Switch(L.Text("pause_pc_monitoring"), paused, value => { paused = value; Dirty(false); }), 2);
        monitorCard.Add(Note(L.Text("wireless_settings_remain_available_while_telemetry_is_paused")));
        monitorCard.Add(Note(L.Text("app_options_moved_note")));
        var help = p.Add(new ClawSection(L.Text("help_card")));
        var logs = new ClawButton() { Text = L.Text("open_logs") }; logs.Click += (_, _) => { if (!preview) Process.Start(new ProcessStartInfo("notepad.exe", '"' + Log.PathName + '"') { UseShellExecute = true }); };
        var about = new ClawButton() { Text = L.Text("about_clawpal") }; about.Click += (_, _) => ShowPage(SettingsPage.About);
        help.Add(Row(0, (logs, 170), (about, 210)));
    }
    private void ConnectionPage()
    {
        title.Text = L.Text("right_here_over_wi_fi"); subtitle.Text = L.Text("your_display_settings_travel_across_your_local_network");
        var p = Stack(); var model = BoardCatalog.Get(config.BoardId);
        // The display, as a label on its own: the model, then the facts at one size in fixed columns.
        var info = p.Add(new ClawCard(model.ToString()));
        info.Add(new FactList(
            (L.Text("fact_firmware"), preview ? Installer.BundledVersion : wireless.FirmwareVersion.Length > 0 ? wireless.FirmwareVersion : L.Text("waiting_for_display")),
            (L.Text("fact_wifi"), preview ? L.Text("home_wi_fi") : wireless.WifiSsid.Length > 0 ? wireless.WifiSsid : L.Text("available_when_connected")),
            (L.Text("fact_screen"), $"{model.Width} × {model.Height}")), 8);
        info.Add(Note(model.Description));

        var wifi = p.Add(new ClawSection(L.Text("change_wi_fi"), L.Text("the_display_must_be_online_it_reconnects_after_this")));
        var ssid = ClawTheme.Input(L.Text("new_2_4_ghz_wi_fi_name"));
        var password = ClawTheme.Input(L.Text("wi_fi_password_leave_blank_for_an_open_network")); password.UseSystemPasswordChar = true;
        var picker = wifi.Add(new WifiPicker(ssid, preview), 16);
        wifi.Add(Row(560, (Column(Caption(L.Text("wi_fi_name_2_4_ghz")), ssid), 0), (Column(Caption(L.Text("password_blank_for_an_open_network")), password), 0)));
        var reveal = new ClawSwitch() { Text = L.Text("show_password") }; reveal.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !reveal.Checked; wifi.Add(reveal);
        var change = new ClawButton() { Text = L.Text("change_network"), Primary = true }; wifi.Add(Row(0, (change, 210)));
        change.Click += async (_, _) =>
        {
            change.Enabled = false; SetMoment("working");
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); await wireless.ChangeWifiAsync(ssid.Text, password.Text, message => status.Text = message, timeout.Token); password.Clear(); status.Text = L.Text("network_saved_the_display_is_reconnecting"); SetMoment("happy"); }
            catch (Exception e) { status.Text = e.Message; SetMoment("error"); }
            finally { if (!change.IsDisposed) change.Enabled = true; }
        };
        _ = picker.LoadAsync();

        var maintenance = p.Add(new ClawSection(L.Text("maintenance"), L.Text("usb_is_for_initial_installation_and_recovery_updates_work")));
        var usb = new ClawButton() { Text = L.Text("usb_setup_recovery") }; usb.Click += (_, _) => OpenInstaller?.Invoke();
        maintenance.Add(Row(0, (usb, 240)));
    }
    private async Task RefreshFirmware()
    {
        if (preview || connection.Updates.Busy) return;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await connection.Updates.ReadDevice(timeout.Token); }
        catch (Exception e) { connection.Updates.ReportProblem(e); }
        if (!IsDisposed) RefreshState();
    }
    private void UpdatesPage()
    {
        title.Text = L.Text("a_little_better_over_wi_fi"); subtitle.Text = L.Text("new_firmware_same_settings_clawpal_checks_releases_you_choose");
        var p = Stack(); var updates = connection.Updates;
        var firmware = p.Add(new ClawCard(L.Text("display_firmware_card")));
        firmwareLabel = firmware.Add(new Label { Font = ClawTheme.Display(18), ForeColor = ClawTheme.Ink, UseMnemonic = false, Height = 40 }, 6);
        updateStatus = firmware.Add(ClawTheme.Label("", 400, ClawTheme.Body, false, 22), 16);
        installUpdate = new ClawButton() { Text = L.Text("install_over_wi_fi"), Primary = true };
        prepareUpdate = new ClawButton() { Text = L.Text("prepare_over_usb_once") };
        var refreshFirmware = new ClawButton() { Text = L.Text("refresh_display") };
        refreshFirmware.Click += async (_, _) => await RefreshFirmware();
        firmware.Add(Row(0, (installUpdate, 240), (prepareUpdate, 240), (refreshFirmware, 180)), 14);
        firmware.Add(Note(L.Text("your_display_restarts_after_the_upload_keep_it_powered")));
        var releases = p.Add(new ClawSection(L.Text("github_releases"), L.Text("official_update_source")));
        var automatic = new ClawSwitch() { Text = L.Text("automatically_check_every_6_hours"), Checked = config.AutoCheckUpdates }; releases.Add(automatic, 6);
        automatic.CheckedChanged += (_, _) => { if (!preview) updates.SetAutomaticChecks(automatic.Checked); };
        releaseStatus = releases.Add(ClawTheme.Label("", 400, ClawTheme.Body, false, 22), 14);
        checkUpdate = new ClawButton() { Text = L.Text("check_github_now") };
        var pcUpdate = new ClawButton() { Text = L.Text("download_pc_update") };
        pcUpdate.Click += (_, _) => { if (!preview) Process.Start(new ProcessStartInfo(updates.ReleasePage.AbsoluteUri) { UseShellExecute = true }); };
        releases.Add(Row(0, (checkUpdate, 210), (pcUpdate, 240)), 14);
        releases.Add(Note(L.Text("official_update_help")));
        checkUpdate.Click += async (_, _) => { if (preview) return; try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await updates.Check(timeout.Token); } catch (Exception e) { updates.ReportProblem(e); status.Text = e.Message; SetMoment("error"); } RefreshState(); };
        installUpdate.Click += async (_, _) =>
        {
            if (preview) return;
            try
            {
                if (deviceDirty || pcDirty) await Save(); if (deviceDirty || pcDirty) throw new IOException(L.Text("save_your_settings_before_updating_firmware"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var task = updates.Install(updates.AvailableVersion is null, timeout.Token); RefreshState(); await task; SetMoment("happy");
            }
            catch (Exception e) { updates.ReportProblem(e); status.Text = e.Message; SetMoment("error"); }
            finally { RefreshState(); }
        };
        prepareUpdate.Click += async (_, _) =>
        {
            if (preview) return;
            try
            {
                if (deviceDirty || pcDirty) await Save(); if (deviceDirty || pcDirty) throw new IOException(L.Text("save_your_settings_before_preparing_wi_fi_updates"));
                var devices = await Task.Run(DeviceDiscovery.Find); var selected = DeviceDiscovery.Preferred(devices, null) ?? throw new IOException(L.Text("connect_only_your_paired_display_with_a_usb_c"));
                var progress = new Progress<string>(text => { status.Text = text; if (updateStatus is not null) updateStatus.Text = text; });
                var task = updates.PrepareUsb(selected.Port, progress, CancellationToken.None); RefreshState(); await task; SetMoment("happy");
            }
            catch (Exception e) { updates.ReportProblem(e); status.Text = e.Message; SetMoment("error"); }
            finally { RefreshState(); }
        };
        RefreshState();
    }
    private async Task LoadDisplay()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var snapshot = await wireless.ReadAsync(timeout.Token);
            if (IsDisposed) return;
            if (!deviceDirty && !config.SettingsPending) { draft = snapshot.Settings; config.RememberDisplaySettings(draft); L.SetVariant(draft.Theme); ShowPage(pageIndex); }
        }
        catch (NotSupportedException e) { if (!IsDisposed) { status.Text = e.Message; SetMoment("error"); } }
        catch (Exception) { if (!IsDisposed) status.Text = L.Text("display_offline_you_can_save_changes_here_and_sync"); }
    }
    private async Task Save()
    {
        if (saving || connection.Updates.Busy) return; saving = true; save.Enabled = false; SetMoment("working");
        try
        {
            draft.Validate(); shortcutDeck.Validate();
            if (preview) { deviceDirty = pcDirty = shortcutsDirty = false; status.Text = L.Text("preview_only_no_settings_changed"); SetMoment("happy"); return; }
            if (pcDirty) { Startup.Set(startup); config.TrackFps = fps; config.AutoOpenApps = auto; config.Paused = paused; config.ClaudeLiveUsage = claudeLive; config.CompanionLanguage = pcLanguage; config.Save(); L.SetLanguage(pcLanguage); connection.SetAutoOpenApps(auto); connection.SetPaused(paused); pcDirty = false; }
            if (shortcutsDirty) { config.SetShortcuts(shortcutDeck); connection.ShortcutsChanged(); shortcutsDirty = false; }
            if (deviceDirty) { config.SetDisplaySettings(draft); deviceDirty = false; }
            if (config.SettingsPending) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(16)); await wireless.SyncAsync(timeout.Token); }
            status.Text = L.Text("saved_your_desk_your_way"); SetMoment("happy");
        }
        catch (Exception e) { status.Text = config.SettingsPending ? L.Text("saved_on_this_pc") + DeviceSettingsClient.FailureMessage(e) : e.Message; SetMoment(e is ArgumentException ? "error" : null); }
        finally { saving = false; save.Enabled = deviceDirty || pcDirty; }
    }
    internal void ShowUpdates() { ShowPage(SettingsPage.Updates); Show(); Activate(); }
    internal void ReportProblem(string message) { status.Text = message; SetMoment("error"); Show(); Activate(); }
    internal void RenderPreviews(string directory)
    {
        ClawTheme.Still = true; Directory.CreateDirectory(directory); Show(); Application.DoEvents();
        void Capture(string name) { Application.DoEvents(); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png); }
        for (int i = 0; i <= (int)SettingsPage.About; i++) { ShowPage((SettingsPage)i); Capture($"settings-{i}"); if (pages.MaximumOffset > 0) { pages.ScrollTo(pages.MaximumOffset); Capture($"settings-{i}-end"); } }
        foreach (var size in new[] { new Size(960, 680), new Size(1600, 900) }) { ClientSize = size; ShowPage(SettingsPage.Home); Capture($"home-{size.Width}"); }
        WindowState = FormWindowState.Maximized; ShowPage(SettingsPage.Home); Capture("home-maximized"); WindowState = FormWindowState.Normal; ClientSize = new(1140, 800);
        foreach (var size in new[] { new Size(960, 680), new Size(1140, 800) }) { ClientSize = size; ShowPage(SettingsPage.Apps); Application.DoEvents(); pages.ScrollTo(pages.MaximumOffset); Capture($"claude-{size.Width}"); }
        ClientSize = new(960, 680); ShowPage(SettingsPage.Shortcuts); Capture("shortcuts-960"); ShowPage(SettingsPage.Screen); Capture("screen-960"); ShowPage(SettingsPage.Clock); Capture("clock-960"); ClientSize = new(1140, 800);
        draft = draft with { Theme = "studio", Accent = "ice" }; ShowPage(SettingsPage.Home); Capture("home-studio"); ShowPage(SettingsPage.Look); Capture("look-studio"); draft = draft with { Theme = Themes.Default, Accent = Themes.DefaultAccent };
        ShowPage(SettingsPage.Clock); Application.DoEvents(); var zones = activePage!.Controls.Cast<Control>().SelectMany(Descendants).OfType<ChoiceBox>().Last(); zones.PerformClick(); Application.DoEvents();
        var list = zones.SelectionList; using (var bitmap = new Bitmap(list.Width, list.Height)) { list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "choice-popup.png"), ImageFormat.Png); }
        zones.SelectionMenu.Close();
        Close();
    }
    private static IEnumerable<Control> Descendants(Control control) { yield return control; foreach (Control child in control.Controls) foreach (var nested in Descendants(child)) yield return nested; }
    protected override void Dispose(bool disposing) { if (disposing) { L.Changed -= LanguageChanged; refresh.Dispose(); momentTimer.Dispose(); } base.Dispose(disposing); }

    /* A page in the rail: plain black type on the case; the page you are on becomes a black tab joined to the screen. */
    private sealed class NavButton : ClawButton
    {
        private readonly System.Windows.Forms.Timer growth = new() { Interval = 15 };
        private float reach = 1;
        private long grownAt;
        internal NavButton()
        {
            Font = ClawTheme.Font(ClawTheme.Lead, true);
            growth.Tick += (_, _) => { float t = Math.Min(1, (Environment.TickCount64 - grownAt) / 200f); reach = 1 - (1 - t) * (1 - t); if (t >= 1) growth.Stop(); Invalidate(); };
        }
        /* The screen reaches out of its edge to take in the page you chose. */
        internal void Grow() { if (!ClawTheme.Motion || !IsHandleCreated) { reach = 1; return; } reach = 0; grownAt = Environment.TickCount64; growth.Start(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ClawTheme.Case);
            if (Hot && !Selected) ClawTheme.Fill(g, ClawTheme.CaseHover, new Rectangle(0, 0, Width, Height));
            int left = (int)(Width * (1 - reach));
            if (Selected) ClawTheme.Fill(g, ClawTheme.Screen, new Rectangle(left, 0, Width - left, Height));
            ClawTheme.Text(g, Text, Font, Selected && reach > .4f ? ClawTheme.Ink : ClawTheme.CaseInk, new Rectangle(LogicalToDeviceUnits(14), 0, Width - LogicalToDeviceUnits(22), Height));
            if (Focused && ShowFocusCues) ClawTheme.Frame(g, Selected ? ClawTheme.Ink : ClawTheme.CaseInk, new Rectangle(2, 2, Width - 4, Height - 4));
        }
        protected override void Dispose(bool disposing) { if (disposing) growth.Dispose(); base.Dispose(disposing); }
    }
    /* An app's icon, name and what it shows, with the switch that turns it on or off. */
    private sealed class AppHeader : Panel
    {
        internal const int TextLeft = 58;
        internal event Action<bool>? Toggled;
        internal ClawSwitch Toggle { get; }
        private readonly DisplayApp app;
        internal AppHeader(DisplayApp app, bool on)
        {
            this.app = app; Height = 60; DoubleBuffered = true;
            Toggle = new ClawSwitch() { Text = "", Checked = on, Size = new(64, 44), AccessibleName = app.Name, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Toggle.CheckedChanged += (_, _) => { Toggled?.Invoke(Toggle.Checked); Invalidate(); }; Controls.Add(Toggle);
            Resize += (_, _) => Toggle.Location = new(Width - Toggle.Width, (Height - Toggle.Height) / 2);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics;
            AppArtwork.Draw(g, app.Id, new Rectangle(0, (Height - 40) / 2, 40, 40));
            ClawTheme.Text(g, app.Name, ClawTheme.Font(ClawTheme.Head, true), Toggle.Checked ? ClawTheme.Ink : ClawTheme.Muted, new Rectangle(TextLeft, 2, Width - TextLeft - 84, 32));
            ClawTheme.Text(g, app.Description, ClawTheme.Font(ClawTheme.Small), ClawTheme.Muted, new Rectangle(TextLeft, 32, Width - TextLeft - 84, 24));
        }
    }
    /* The product name at the size of a sign, with its version beside it. */
    private sealed class AboutMark(string name, string version) : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
            var big = ClawTheme.Display(54); var size = TextRenderer.MeasureText(g, name, big, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, name, big, new Point(-LogicalToDeviceUnits(3), 0), ClawTheme.Ink, TextFormatFlags.NoPadding);
            ClawTheme.Text(g, version, ClawTheme.Display(18), ClawTheme.Accent, new Rectangle(size.Width + LogicalToDeviceUnits(14), 0, Width, size.Height - LogicalToDeviceUnits(10)), TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
        }
    }
}

/* Text on the case that lets the window take the mouse, so the rail still drags the window. A dot can mark a live state. */
internal sealed class CaseLabel : Label
{
    internal bool Dot { get; set; }
    internal bool Lit { get; set; }
    public CaseLabel() { ForeColor = ClawTheme.CaseInk; UseMnemonic = false; BackColor = ClawTheme.Case; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } base.WndProc(ref m); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor); int left = 0;
        if (Dot)
        {
            // A square pixel: solid while the display is connected, an empty square while it is not.
            int size = LogicalToDeviceUnits(10), top = LogicalToDeviceUnits(5); left = size + LogicalToDeviceUnits(10);
            if (Lit) ClawTheme.Fill(g, ClawTheme.CaseInk, new Rectangle(0, top, size, size)); else ClawTheme.Frame(g, ClawTheme.CaseInk, new Rectangle(0, top, size, size));
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left, Height), ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/* A choice shown as itself: the value large, what it means small beneath. */
internal sealed class BigChoice : ClawButton
{
    internal string Big { get; set; }
    internal BigChoice(string big, string caption) { Big = big; Text = caption; Height = 92; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        var (fill, ink) = Colors(); ClawTheme.Fill(g, fill, new Rectangle(0, 0, Width, Height));
        int pad = LogicalToDeviceUnits(18), split = Height * 60 / 100;
        ClawTheme.Text(g, Big, ClawTheme.Display(24), ink, new Rectangle(pad, 0, Width - 2 * pad, split), TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        bool lit = (Primary || Selected) && Enabled;
        ClawTheme.Text(g, Text, ClawTheme.Font(ClawTheme.Small, lit), lit ? ink : Enabled ? ClawTheme.Muted : ClawTheme.Faint, new Rectangle(pad, split + LogicalToDeviceUnits(6), Width - 2 * pad, Height - split - LogicalToDeviceUnits(8)), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        PaintFocus(g);
    }
}

/* Facts at one size, the names in a fixed column and the values starting together beside them. */
internal sealed class FactList : Control
{
    private readonly (string Name, string Value)[] facts;
    internal FactList(params (string Name, string Value)[] facts) { this.facts = facts; Height = facts.Length * 34; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? ClawTheme.Screen);
        var font = ClawTheme.Font(ClawTheme.Lead); int row = Height / Math.Max(1, facts.Length), column = Math.Min(LogicalToDeviceUnits(170), Width / 3);
        for (int i = 0; i < facts.Length; i++)
        {
            ClawTheme.Text(g, facts[i].Name, font, ClawTheme.Muted, new Rectangle(0, i * row, column - 8, row));
            ClawTheme.Text(g, facts[i].Value, font, ClawTheme.Ink, new Rectangle(column, i * row, Width - column, row));
        }
    }
}
