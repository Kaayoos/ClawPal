using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ClawPal.Companion;

internal sealed class SettingsForm : ClawWindow
{
    /* Sidebar order: what the display shows, then this PC. About opens from the version label. */
    private enum SettingsPage { Home, Apps, Shortcuts, Clock, Screen, Look, Pc, Connection, Updates, About }
    private static readonly SettingsPage[][] Groups = [[SettingsPage.Home, SettingsPage.Apps, SettingsPage.Shortcuts, SettingsPage.Clock, SettingsPage.Screen, SettingsPage.Look], [SettingsPage.Pc, SettingsPage.Connection, SettingsPage.Updates]];
    private readonly Configuration config;
    private readonly Connection connection;
    private readonly DeviceSettingsClient wireless;
    private DeviceSettings draft;
    private string pcLanguage;
    private bool fps,auto,startup,paused,claudeLive,deviceDirty,pcDirty,saving,shortcutsDirty;
    private ShortcutDeck shortcutDeck;
    private readonly bool preview;
    private readonly Panel sidebar=new(){Dock=DockStyle.Left,Width=210,BackColor=ClawTheme.Surface};
    private readonly ClawScrollPanel pages=new(){Dock=DockStyle.Fill,Padding=new Padding(30,12,28,20)};
    private readonly ClawHeading title=new(){Font=ClawTheme.Font(24,true),ForeColor=ClawTheme.Ink,Height=46};
    private readonly Label subtitle=ClawTheme.Label("",700,10,false,38),status=ClawTheme.Label("",640,9,false,50),link=ClawTheme.Label("",184,9,false,44);
    private readonly ClawButton save=new(){Text=L.Text("save_changes"),Primary=true,Width=156};
    private readonly CrabView crab=new(){Size=new(144,144),State="idle"};
    private readonly Dictionary<SettingsPage,ClawButton> navigation=new();
    private readonly List<Label> groupLabels=new();
    private readonly System.Windows.Forms.Timer refresh=new(){Interval=1000};
    private Label? updateStatus,releaseStatus,firmwareLabel;
    private ClawButton? installUpdate,prepareUpdate,checkUpdate;
    private Control? activePage;
    private SettingsPage pageIndex;
    private HomePreview? homePreview;
    private HomeOrderList? orderList;
    private ClawWrap? addApps;
    private Label? addNote;
    internal event Action? OpenInstaller;
    internal SettingsForm(Configuration config,Connection connection,bool preview=false)
    {
        this.config=config;this.connection=connection;pcLanguage=L.Normalize(config.CompanionLanguage);this.preview=preview;wireless=connection.Settings;
        draft=config.DisplaySettings??DeviceSettings.FromConfiguration(config);shortcutDeck=config.Shortcuts??ShortcutDeck.Starter(draft.Language);fps=config.TrackFps;auto=config.AutoOpenApps;paused=config.Paused;claudeLive=config.ClaudeLiveUsage;startup=!preview&&Startup.Enabled;
        Text=L.Text("clawpal_your_desk_companion");ClientSize=new(1120,790);AutoScaleMode=AutoScaleMode.Dpi;
        using(var stream=typeof(SettingsForm).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon"))if(stream is not null)Icon=new Icon(stream);
        var content=new Panel(){Dock=DockStyle.Fill};Controls.Add(content);Controls.Add(sidebar);
        var brand=ClawTheme.Label(L.Text("clawpal"),170,25,true,46);brand.Location=new(24,22);sidebar.Controls.Add(brand);
        link.Location=new(26,70);link.Height=36;sidebar.Controls.Add(link);
        int y=NavTop;string[] groupNames=[L.Text("nav_group_display"),L.Text("nav_group_pc")];
        for(int g=0;g<Groups.Length;g++){
            var heading=ClawTheme.Label(groupNames[g].ToUpperInvariant(),170,8,true,GroupRow);heading.ForeColor=ClawTheme.Muted;heading.TextAlign=ContentAlignment.BottomLeft;heading.Location=new(28,y);sidebar.Controls.Add(heading);groupLabels.Add(heading);y+=GroupRow;
            foreach(var page in Groups[g]){var button=new NavButton(){Text=PageName(page),Size=new(174,NavRow),Location=new(18,y)};button.Click+=(_,_)=>ShowPage(page);navigation[page]=button;sidebar.Controls.Add(button);y+=NavRow;}
            y+=6;
        }
        sidebar.Controls.Add(crab);sidebar.Resize+=(_,_)=>PlaceCrab();
        ClawTheme.Changed+=LookChanged;
        var version=ClawTheme.Label(L.Text("clawpal_94dbc7")+typeof(SettingsForm).Assembly.GetName().Version?.ToString(3),170,9,false,30);version.Location=new(25,sidebar.Height-48);version.Anchor=AnchorStyles.Left|AnchorStyles.Bottom;sidebar.Controls.Add(version);
        version.Cursor=Cursors.Hand;version.AccessibleName=L.Text("about_clawpal");
        version.Click+=(_,_)=>ShowPage(SettingsPage.About);version.TabStop=true;
        version.KeyDown+=(_,e)=>{if(e.KeyCode is Keys.Enter or Keys.Space){ShowPage(SettingsPage.About);e.Handled=true;}};
        var aboutHint=new ToolTip();aboutHint.SetToolTip(version,L.Text("about_clawpal"));Disposed+=(_,_)=>aboutHint.Dispose();
        // The community link stays in view on every page, just above the version.
        var discord=new ClawButton(){Text=L.Text("join_discord"),Brand=Color.FromArgb(0x58,0x65,0xF2),Size=new(174,40),Location=new(18,sidebar.Height-DiscordInset),Anchor=AnchorStyles.Left|AnchorStyles.Bottom};
        discord.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo(DiscordInvite){UseShellExecute=true});};sidebar.Controls.Add(discord);
        var header=new Panel(){Dock=DockStyle.Top,Height=106,Padding=new Padding(30,26,28,0)};
        title.Dock=DockStyle.Top;subtitle.Dock=DockStyle.Fill;header.Controls.Add(subtitle);header.Controls.Add(title);
        var footer=new Panel(){Dock=DockStyle.Bottom,Height=84,Padding=new Padding(30,12,28,20)};
        save.Anchor=AnchorStyles.Right|AnchorStyles.Top;save.Top=18;status.Location=new(30,14);footer.Controls.AddRange([status,save]);
        footer.Resize+=(_,_)=>{save.Left=footer.Width-save.Width-28;status.Width=Math.Max(180,save.Left-48);};
        content.Controls.Add(pages);content.Controls.Add(header);content.Controls.Add(footer);
        save.Click+=async(_,_)=>await Save();
        refresh.Tick+=(_,_)=>RefreshState();refresh.Start();
        Shown+=async(_,_)=>{if(!preview){await LoadDisplay();await RefreshFirmware();}};
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing && !preview){e.Cancel=true;Hide();}};
        L.Changed+=LanguageChanged;ShowPage(SettingsPage.Home);RefreshState();
    }
    private const string DiscordInvite="https://discord.com/invite/gR5DqCdGre";
    internal const int NavTop=112,NavRow=38,GroupRow=28,DiscordInset=96;
    private static string PageName(SettingsPage page)=>page switch{
        SettingsPage.Home=>L.Text("home"),SettingsPage.Apps=>L.Text("apps"),SettingsPage.Shortcuts=>L.Text("shortcuts"),SettingsPage.Clock=>L.Text("clock_weather"),
        SettingsPage.Screen=>L.Text("screen"),SettingsPage.Look=>L.Text("look_language"),SettingsPage.Pc=>L.Text("pc_app_nav"),SettingsPage.Connection=>L.Text("connection_nav"),
        SettingsPage.Updates=>L.Text("updates"),_=>L.Text("about_clawpal")};
    /* The crab rests between the navigation and the Discord button when there is room and the look has one. */
    private void PlaceCrab()
    {
        int top=navigation.Values.Max(b=>b.Bottom)+8,bottom=sidebar.Height-DiscordInset-8;
        int size=Math.Min(144,Math.Max(0,bottom-top)/48*48);crab.Visible=size>=48&&ClawTheme.Mascot;
        if(size>=48)crab.SetBounds((sidebar.Width-size)/2,bottom-size,size,size);
    }
    private void LookChanged(){if(IsDisposed)return;PlaceCrab();homePreview?.ApplyLook();Invalidate(true);}
    private void RefreshState()
    {
        bool online=preview||connection.IsConnected;
        var updates=connection.Updates;
        if(updateStatus is not null)updateStatus.Text=preview?L.Text("ready_for_wi_fi_updates_your_settings_stay_on"):updates.Status;
        if(releaseStatus is not null)releaseStatus.Text=preview?L.Text("official_update_source"):updates.ReleaseStatus;
        if(firmwareLabel is not null)firmwareLabel.Text=L.Text("versions", preview?Installer.BundledVersion:updates.Device?.Version??L.Text("not_connected"), typeof(SettingsForm).Assembly.GetName().Version?.ToString(3));
        if(installUpdate is not null){installUpdate.Visible=updates.Device?.OtaReady!=false;installUpdate.Enabled=!updates.Busy&&!updates.Checking&&updates.Device?.OtaReady==true&&!updates.CompanionRequired&&(updates.AvailableVersion is not null||updates.BundledNewer);installUpdate.Text=updates.AvailableVersion is { } v?L.Text("install_version", v):L.Text("install_bundled_update");}
        if(prepareUpdate is not null){prepareUpdate.Visible=updates.Device?.OtaReady==false;prepareUpdate.Enabled=!updates.Busy;}
        if(checkUpdate is not null)checkUpdate.Enabled=!updates.Busy&&!updates.Checking;
        var updatesName=updates.NotificationVersion is null?L.Text("updates"):L.Text("updates_1");if(navigation[SettingsPage.Updates].Text!=updatesName)navigation[SettingsPage.Updates].Text=updatesName;
        if(updates.Busy)crab.State="working";
        pages.Enabled=!updates.Busy;
        link.Text=updates.Busy?L.Text("updating_your_display"):config.Paused?L.Text("pc_monitoring_paused"):online?L.Text("connected_over_wi_fi"):L.Text("display_offline_settings_are_still_available");link.ForeColor=online?ClawTheme.Accent:ClawTheme.Muted;
        save.Enabled=!saving&&!updates.Busy&&(deviceDirty||pcDirty);
        if(updates.Busy){status.Text=updates.Status;return;}
        if(!saving&&!deviceDirty&&!pcDirty)status.Text=preview?L.Text("preview_changes_shown_here_are_illustrative"):wireless.NeedsFirmwareUpdate?L.Text("open_updates_to_install_the_firmware_for_these_settings"):config.SettingsPending?wireless.Status:config.Paused?L.Text("pc_monitoring_is_paused_display_settings_can_still_sync"):online?L.Text("all_changes_saved_make_yourself_at_home"):L.Text("display_offline_saved_changes_will_sync_when_it_returns");
    }
    private void Dirty(bool device)
    {
        if(device)deviceDirty=true;else pcDirty=true;status.Text=L.Text("you_have_unsaved_changes");save.Enabled=!saving;crab.State="idle";
    }
    private void SetDraft(DeviceSettings value)
    {
        draft=value;Dirty(true);homePreview?.SetSettings(draft);orderList?.SetSettings(draft);RefreshAddApps();
    }
    private void Clear()
    {
        homePreview=null;orderList=null;addApps=null;addNote=null;updateStatus=null;releaseStatus=null;firmwareLabel=null;installUpdate=null;prepareUpdate=null;checkUpdate=null;
        foreach(Control child in pages.Content.Controls.Cast<Control>().ToArray())child.Dispose();
    }
    /* A page whose parts arrange themselves by hand (Home and Shortcuts, which place a preview beside an editor). */
    private Panel Page(int height=510)
    {
        Clear();var p=new Panel(){Dock=DockStyle.Top,Width=pages.Content.Width,Height=height,BackColor=ClawTheme.Background};
        pages.Content.Controls.Add(p);activePage=p;pages.ResetScroll();return p;
    }
    /* Every other page: titled cards, one above the other. */
    private ClawStack Cards()
    {
        Clear();var p=new ClawStack(){Dock=DockStyle.Top,Width=pages.Content.Width,Gap=16,BackColor=ClawTheme.Background,Padding=new Padding(0,4,0,8)};
        pages.Content.Controls.Add(p);activePage=p;pages.ResetScroll();return p;
    }
    private static Label Note(string text,float size=9)=>ClawTheme.Label(text,400,size,false,20);
    private static Label Caption(string text)=>ClawTheme.Label(text,400,10,true,24);
    private static ClawSwitch Switch(string text,bool value,Action<bool> change)
    {
        var control=new ClawSwitch(){Text=text,Checked=value,Height=40};control.CheckedChanged+=(_,_)=>change(control.Checked);return control;
    }
    private static ClawRow Row(int stackBelow=0,params (Control control,int width)[] items)
    {
        var row=new ClawRow(){StackBelow=stackBelow};foreach(var (control,width) in items)row.Add(control,width);return row;
    }
    private static ClawStack Column(params Control[] items){var column=new ClawStack(){Gap=8,BackColor=ClawTheme.Surface};foreach(var item in items)column.Add(item);return column;}
    private void ShowPage(SettingsPage index)
    {
        pageIndex=index;foreach(var (page,button) in navigation){button.Selected=page==index;button.Invalidate();}
        switch(index){
            case SettingsPage.Home:HomePage();break;case SettingsPage.Apps:AppsPage();break;case SettingsPage.Shortcuts:ShortcutsPage();break;
            case SettingsPage.Clock:ClockPage();break;case SettingsPage.Screen:ScreenPage();break;case SettingsPage.Look:LookPage();break;
            case SettingsPage.Pc:PcPage();break;case SettingsPage.Connection:ConnectionPage();break;case SettingsPage.Updates:UpdatesPage();break;default:AboutPage();break;
        }
    }

    private void LanguageChanged(){if(IsDisposed)return;pcLanguage=L.Language;L.RefreshControls(this);foreach(var (page,button) in navigation)button.Text=PageName(page);string[] groupNames=[L.Text("nav_group_display"),L.Text("nav_group_pc")];for(int i=0;i<groupLabels.Count;i++)groupLabels[i].Text=groupNames[i].ToUpperInvariant();ShowPage(pageIndex);RefreshState();}
    private void AboutPage()
    {
        title.Text=L.Text("about_clawpal");subtitle.Text=L.Text("created_by_minimalpulse");var p=Cards();
        ClawButton Link(string caption,string url){var b=new ClawButton{Text=caption,Height=42};b.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo(url){UseShellExecute=true});};return b;}
        var about=p.Add(new ClawCard(L.Text("companion_version_value",typeof(SettingsForm).Assembly.GetName().Version?.ToString(3))));
        about.Add(Row(520,(Link("minimalpulse.com","https://minimalpulse.com/"),0),(Link("github.com/Kaayoos/clawpal","https://github.com/Kaayoos/clawpal"),0)));
        about.Add(Note(L.Text("about_license"),10));
        var credits=p.Add(new ClawCard(L.Text("about_credits_title")));
        credits.Add(Note(L.Text("about_weather")));credits.Add(Note(L.Text("about_tools")));credits.Add(Note(L.Text("about_marks")));
        var licenses=new ClawButton{Text=L.Text("open_licenses"),Height=42};
        licenses.Click+=(_,_)=>{if(preview)return;Installer.PreparePayload();Process.Start(new ProcessStartInfo(Path.Combine(Installer.PayloadPath,"licenses","THIRD_PARTY_NOTICES.txt")){UseShellExecute=true});};
        credits.Add(Row(0,(licenses,230)));
        p.Add(Note(L.Text("about_device_hint")));
    }

    /* The look applies to this app at once, so the choice can be judged before saving it to the display. */
    private void LookPage()
    {
        title.Text=L.Text("look_language_title");subtitle.Text=L.Text("look_language_description");var p=Cards();
        var lookCard=p.Add(new ClawCard(L.Text("look_section")));
        var picker=new LookPicker(draft.Theme,draft.Accent){BackColor=ClawTheme.Surface};lookCard.Add(picker);
        picker.Changed+=(theme,accent)=>{SetDraft(draft with{Theme=theme,Accent=accent});ClawTheme.Use(theme,accent);};
        lookCard.Add(Note(L.Text("appearance_restart_note")));
        var languageCard=p.Add(new ClawCard(L.Text("language")));
        ClawStack Picker(string caption,string code,Action<string> change){
            var box=new ChoiceBox();foreach(var language in L.Languages)box.Items.Add(language);
            box.SelectedIndex=L.Languages.ToList().FindIndex(l=>l.Code==code);
            box.SelectedIndexChanged+=(_,_)=>{if(box.SelectedItem is LanguageChoice language)change(language.Code);};
            return Column(Caption(caption),box);
        }
        languageCard.Add(Row(560,(Picker(L.Text("pc_language"),pcLanguage,code=>{pcLanguage=code;Dirty(false);}),0),(Picker(L.Text("display_language"),draft.Language,code=>SetDraft(draft with{Language=code})),0)));
        languageCard.Add(Note(L.Text("language_note")));
    }

    private void HomePage()
    {
        title.Text=L.Text("a_place_for_your_favorites");subtitle.Text=L.Text("arrange_your_shortcuts_your_crab_keeps_the_best_seat");
        var p=Page();homePreview=new(draft,config.BoardId);orderList=new(draft){Font=ClawTheme.Font()};p.Controls.AddRange([homePreview,orderList]);
        var previewCaption=ClawTheme.Label(L.Text("your_display_dimensions",BoardCatalog.Get(config.BoardId).Width,BoardCatalog.Get(config.BoardId).Height),390,9,false,24);
        var listCaption=ClawTheme.Label(L.Text("on_your_home"),300,13,true,30);
        var instruction=ClawTheme.Label(L.Text("drag_to_reorder_the_minus_removes_a_shortcut"),330,9,false,38);
        var hint=ClawTheme.Label(L.Text("apps_stay_available_in_the_display_s_apps_menu"),395,10,false,65);
        var addCaption=ClawTheme.Label(L.Text("add_to_home"),300,13,true,30);
        addNote=ClawTheme.Label("",330,9,false,38);addApps=new ClawWrap(){ItemWidth=146,ItemHeight=50,Gap=8};
        p.Controls.AddRange([previewCaption,listCaption,instruction,hint,addCaption,addNote,addApps]);
        orderList.Changed+=SetDraft;
        void Layout(){
            if(homePreview is null||orderList is null||addApps is null||addNote is null)return;
            bool sideBySide=p.Width>=790;
            int left=sideBySide?Math.Min(700,(int)(p.Width*.59)):Math.Min(560,p.Width);
            var model=BoardCatalog.Get(config.BoardId);homePreview.SetBounds(0,28,left,left*model.Height/model.Width);previewCaption.SetBounds(0,0,left,24);
            hint.SetBounds(0,homePreview.Bottom+18,left,58);
            int x=sideBySide?left+28:0,y=sideBySide?0:hint.Bottom+24,right=sideBySide?p.Width-x:left;
            listCaption.SetBounds(x,y,right,30);instruction.SetBounds(x,y+34,right,40);
            int listHeight=Math.Max(62,draft.HomeOrder.Length*62);orderList.SetBounds(x,y+80,right,listHeight);
            int addTop=orderList.Bottom+22;addCaption.SetBounds(x,addTop,right,30);
            addNote.SetBounds(x,addTop+34,right,ClawStack.TextHeight(addNote,right));
            addApps.SetBounds(x,addNote.Bottom+8,right,addApps.Height);addApps.PerformLayout();
            p.Height=Math.Max(hint.Bottom,addApps.Bottom)+24;
        }
        addApps.Resize+=(_,_)=>Layout();p.Resize+=(_,_)=>Layout();orderList.Changed+=_=>Layout();RefreshAddApps();Layout();
    }
    /* Every enabled app not yet on Home, as a tile that adds it. */
    private void RefreshAddApps()
    {
        if(addApps is null||addNote is null)return;
        foreach(Control chip in addApps.Controls.Cast<Control>().ToArray())chip.Dispose();
        bool full=draft.HomeOrder.Length>=5;
        var waiting=DisplayApps.All.Where(a=>draft.Enabled(a.Id)&&!draft.HomeOrder.Contains(a.Id)).ToArray();
        addNote.Text=waiting.Length==0?L.Text("all_enabled_apps_are_on_home"):full?L.Text("home_is_full_remove_one"):L.Text("click_an_app_to_add_it");
        foreach(var app in waiting){
            var chip=new AppChip(app){Enabled=!full};
            chip.Click+=(_,_)=>{if(draft.HomeOrder.Length<5&&!draft.HomeOrder.Contains(app.Id))SetDraft(draft with{HomeOrder=draft.HomeOrder.Append(app.Id).ToArray()});};
            addApps.Controls.Add(chip);
        }
        addApps.PerformLayout();
    }
    /* Each app with its switch, and the options that belong to it right underneath. */
    private void AppsPage()
    {
        title.Text=L.Text("a_little_less_or_all_of_it");subtitle.Text=L.Text("choose_the_apps_available_on_your_display_home_stays");
        var p=Cards();
        var general=p.Add(new ClawCard(L.Text("auto_open_title")));
        general.Add(Switch(L.Text("automatically_open_spotify_or_codex"),auto,value=>{auto=value;Dirty(false);}),2);
        general.Add(Note(L.Text("when_an_enabled_app_opens_on_your_pc_show")));
        foreach(var app in DisplayApps.All){
            var card=p.Add(new ClawCard());
            var header=new AppHeader(app,draft.Enabled(app.Id));header.Toggled+=on=>SetDraft(draft.SetEnabled(app.Id,on));card.Add(header);
            if(app.Id==0){
                card.Add(Switch(L.Text("track_game_fps_in_pc_monitor"),fps,value=>{fps=value;Dirty(false);}),2);
                card.Add(Note(L.Text("uses_windows_frame_events_through_presentmon_game_files_stay")));
            }
            else if(app.Id==7)ClaudeOptions(card);
            else if(app.Id==8){var edit=new ClawButton{Text=L.Text("edit_shortcut_tiles"),Height=40};edit.Click+=(_,_)=>ShowPage(SettingsPage.Shortcuts);card.Add(Row(0,(edit,220)));}
            else if(app.Id is 1 or 6){var edit=new ClawButton{Text=L.Text("clock_weather_settings"),Height=40};edit.Click+=(_,_)=>ShowPage(SettingsPage.Clock);card.Add(Row(0,(edit,260)));}
        }
        p.Add(Note(L.Text("turning_an_app_off_also_removes_its_home_shortcut")));
    }
    private void ClaudeOptions(ClawCard card)
    {
        card.Add(Caption(L.Text("claude_link_title")),4);
        card.Add(Note(L.Text("claude_link_description")));
        var claudeLink=new ClawButton(){Text=L.Text("claude_link_button"),Height=40};
        var claudeUnlink=new ClawButton(){Text=L.Text("claude_link_disconnect"),Height=40};
        card.Add(Row(520,(claudeLink,0),(claudeUnlink,0)));
        var claudeStatus=card.Add(Note(preview?L.Text("claude_link_ready"):ClaudeBridge.Status));
        var liveSwitch=card.Add(Switch(L.Text("claude_live_limits"),claudeLive,value=>{claudeLive=value;Dirty(false);}),2);
        card.Add(Note(L.Text("claude_live_description")));
        claudeLink.Click+=(_,_)=>{try{if(!preview)ClaudeBridge.Install();claudeStatus.Text=L.Text("claude_link_ready");crab.State="happy";}catch(Exception error){claudeStatus.Text=error.Message;crab.State="error";}};
        claudeUnlink.Click+=(_,_)=>{try{if(!preview){ClaudeBridge.Uninstall();config.ClaudeLiveUsage=false;config.Save();}liveSwitch.Checked=false;claudeLive=false;claudeStatus.Text=L.Text("claude_link_removed");crab.State="idle";}catch(Exception error){claudeStatus.Text=error.Message;crab.State="error";}};
    }
    private sealed record DeckSize(int Tiles){public override string ToString()=>L.Text("n_tiles",Tiles);}
    /* The deck as the display shows it, beside everything about the chosen tile. */
    private void ShortcutsPage()
    {
        title.Text=L.Text("your_pc_at_a_tap");subtitle.Text=L.Text("tiles_on_your_display_that_run_things_on_this_pc");
        var p=Page(640);
        var sizeCaption=ClawTheme.Label(L.Text("grid"),90,10,true,44);
        var size=new ChoiceBox(){Width=170,Height=44};foreach(int n in ShortcutDeck.Sizes)size.Items.Add(new DeckSize(n));
        size.SelectedIndex=Array.IndexOf(ShortcutDeck.Sizes,shortcutDeck.Size);
        var restore=new ClawButton(){Text=L.Text("restore_starter_shortcuts"),Size=new(220,44)};
        var deckPreview=new DeckPreview(shortcutDeck,config.BoardId,()=>draft.Language);
        var hint=ClawTheme.Label(L.Text("click_a_tile_to_edit_it_drag_to_move_it"),400,9,false,44);
        var state=ClawTheme.Label("",400,9,false,44);
        p.Controls.AddRange([sizeCaption,hint,state]);
        ShortcutInspector? inspector=null;
        void Change(ShortcutDeck deck){shortcutDeck=deck;shortcutsDirty=true;Dirty(false);deckPreview.SetDeck(deck);ShowState();}
        // Says whether the deck can be saved, and if not, why.
        void ShowState(){try{shortcutDeck.Validate();state.Text=draft.Enabled(8)?"":L.Text("shortcuts_are_off_in_apps");state.ForeColor=ClawTheme.Muted;}catch(ArgumentException e){state.Text=e.Message;state.ForeColor=ClawTheme.Warning;}}
        inspector=new ShortcutInspector(()=>shortcutDeck,()=>draft.Language,Change);
        deckPreview.Changed+=deck=>{Change(deck);inspector.Bind(deckPreview.Selected);};
        deckPreview.SelectedChanged+=slot=>inspector.Bind(slot);
        size.SelectedIndexChanged+=(_,_)=>{
            if(size.SelectedItem is not DeckSize choice||choice.Tiles==shortcutDeck.Size)return;
            try{Change(shortcutDeck.Resize(choice.Tiles));deckPreview.Select(Math.Min(deckPreview.Selected,choice.Tiles-1));Layout();}
            catch(ArgumentException e){state.Text=e.Message;state.ForeColor=ClawTheme.Warning;BeginInvoke(()=>size.SelectedIndex=Array.IndexOf(ShortcutDeck.Sizes,shortcutDeck.Size));}
        };
        restore.Click+=(_,_)=>{Change(ShortcutDeck.Starter(draft.Language));size.SelectedIndex=Array.IndexOf(ShortcutDeck.Sizes,shortcutDeck.Size);deckPreview.Select(0);};
        p.Controls.AddRange([size,restore,deckPreview,inspector]);
        void Layout(){
            bool sideBySide=p.Width>=790;
            int left=sideBySide?Math.Min(560,(int)(p.Width*.58)):Math.Min(560,p.Width);
            var model=BoardCatalog.Get(config.BoardId);bool tiny=model.Height<400;
            int rows=(shortcutDeck.Size+(shortcutDeck.Size==6||(tiny&&shortcutDeck.Size==12)?3:4)-1)/(shortcutDeck.Size==6||(tiny&&shortcutDeck.Size==12)?3:4);
            int previewHeight=tiny?Math.Min(left,left*((model.Height-44)*((rows+1)/2)+44)/model.Width):left*model.Height/model.Width;
            sizeCaption.SetBounds(0,11,70,30);size.SetBounds(74,0,170,44);restore.SetBounds(left-220,0,220,44);
            deckPreview.SetBounds(0,58,left,previewHeight);hint.SetBounds(0,deckPreview.Bottom+12,left,40);state.SetBounds(0,hint.Bottom,left,44);
            int x=sideBySide?left+32:0,y=sideBySide?0:state.Bottom+16;
            inspector.SetBounds(x,y,sideBySide?p.Width-x:left,inspector.Height);
            p.Height=Math.Max(state.Bottom,inspector.Bottom)+24;
        }
        inspector.Resize+=(_,_)=>Layout();p.Resize+=(_,_)=>Layout();Layout();
        deckPreview.Select(Math.Clamp(deckPreview.Selected,0,shortcutDeck.Size-1));ShowState();
    }
    private void ClockPage()
    {
        title.Text=L.Text("your_time_your_little_forecast");subtitle.Text=L.Text("time_and_weather_are_saved_on_the_display_and");
        var p=Cards();
        var clock=p.Add(new ClawCard(L.Text("time_format")));
        var twentyFour=new ClawButton(){Text=L.Text("24_hours_17_34"),Selected=draft.Use24h,Height=44};
        var twelve=new ClawButton(){Text=L.Text("12_hours_5_34_pm"),Selected=!draft.Use24h,Height=44};
        clock.Add(Row(0,(twentyFour,200),(twelve,210)));
        void Hours(bool value){SetDraft(draft with{Use24h=value});twentyFour.Selected=value;twelve.Selected=!value;twentyFour.Invalidate();twelve.Invalidate();}
        twentyFour.Click+=(_,_)=>Hours(true);twelve.Click+=(_,_)=>Hours(false);

        var weather=p.Add(new ClawCard(L.Text("weather_card")));
        weather.Add(Switch(L.Text("show_minimal_weather_in_clock"),draft.WeatherEnabled,value=>SetDraft(draft with{WeatherEnabled=value})));
        weather.Add(Caption(L.Text("weather_location")),6);
        var city=ClawTheme.Input(L.Text("find_a_city_or_village"));city.Text=draft.City=="Weather"?"":draft.City;
        var search=new ClawButton(){Text=L.Text("find_location"),Height=44};
        weather.Add(Row(0,(city,0),(search,170)),8);
        var choices=new ChoiceBox();
        choices.Items.Add(new Place(draft.City,"","",draft.Latitude,draft.Longitude,""));choices.SelectedIndex=0;weather.Add(choices,6);
        var feedback=weather.Add(Note(L.Text("search_then_choose_the_location_that_matches")));
        weather.Add(Caption(L.Text("weather_temperature_unit")),6);
        var celsius=new ClawButton{Text=L.Text("celsius"),Selected=!draft.WeatherFahrenheit,Height=44};
        var fahrenheit=new ClawButton{Text=L.Text("fahrenheit"),Selected=draft.WeatherFahrenheit,Height=44};
        weather.Add(Row(0,(celsius,200),(fahrenheit,210)),6);
        void Units(bool useFahrenheit){SetDraft(draft with{WeatherFahrenheit=useFahrenheit});celsius.Selected=!useFahrenheit;fahrenheit.Selected=useFahrenheit;celsius.Invalidate();fahrenheit.Invalidate();}
        celsius.Click+=(_,_)=>Units(false);fahrenheit.Click+=(_,_)=>Units(true);
        weather.Add(Note(L.Text("weather_unit_scope")));

        var zoneCard=p.Add(new ClawCard(L.Text("time_zone")));
        var zones=new ChoiceBox();TimeZones.Fill(zones,draft.Timezone);zoneCard.Add(zones);
        zones.SelectedIndexChanged+=(_,_)=>{if(zones.SelectedItem is TimeZoneChoice zone)SetDraft(draft with{Timezone=zone.Posix});};
        choices.SelectedIndexChanged+=(_,_)=>{if(choices.SelectedItem is Place place){SetDraft(draft with{City=place.Name,Latitude=place.Latitude,Longitude=place.Longitude});TimeZones.SelectFor(zones,place);}};
        search.Click+=async(_,_)=>{
            if(city.Text.Trim().Length<2){feedback.Text=L.Text("type_at_least_two_letters");return;}search.Enabled=false;feedback.Text=L.Text("looking_for_your_corner_of_the_world");crab.State="thinking";
            try{
                using var http=new HttpClient(){Timeout=TimeSpan.FromSeconds(12)};
                using var json=JsonDocument.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=8&language="+Uri.EscapeDataString(L.Language)+"&name="+Uri.EscapeDataString(city.Text.Trim())));
                if(IsDisposed||choices.IsDisposed)return;
                choices.Items.Clear();choices.SelectedIndex=-1;
                if(json.RootElement.TryGetProperty("results",out var places))foreach(var place in places.EnumerateArray()){
                    string Str(string key)=>place.TryGetProperty(key,out var v)?v.GetString()??"":"";
                    choices.Items.Add(new Place(Str("name"),Str("country"),Str("admin1"),place.GetProperty("latitude").GetDouble(),place.GetProperty("longitude").GetDouble(),Str("timezone")));
                }
                if(choices.Items.Count>0)choices.SelectedIndex=0;
                feedback.Text=choices.Items.Count>0?L.Text("choose_your_location_from_the_results"):L.Text("no_match_try_a_nearby_town");crab.State=choices.Items.Count>0?"idle":"error";
            }catch(Exception){if(!feedback.IsDisposed)feedback.Text=L.Text("location_search_is_unavailable_check_your_internet_connection_and");crab.State="error";}
            finally{if(!search.IsDisposed)search.Enabled=true;}
        };
        p.Add(Note(L.Text("weather_by_open_meteo_location_search_by_geonames")));
    }
    private sealed record SettingChoice(int Value,string Label){public override string ToString()=>Label;}
    private void ScreenPage()
    {
        title.Text=L.Text("a_screen_that_settles_with_you");subtitle.Text=L.Text("touch_to_wake_these_preferences_work_on_every_app");
        var p=Cards();
        var dim=p.Add(new ClawCard(L.Text("automatic_dimming")));
        var modes=new[]{(1,L.Text("pc_offline")),(2,L.Text("whenever_idle")),(0,L.Text("never"))};
        var modeButtons=modes.Select(mode=>new ClawButton{Text=mode.Item2,Selected=draft.DimBehavior==mode.Item1,AccessibleName=L.Text("dim")+mode.Item2,Height=44}).ToArray();
        dim.Add(Row(0,modeButtons.Select(b=>((Control)b,0)).ToArray()),8);
        var behaviorNote=dim.Add(Note(""));
        var delay=new ClawTimeInput{Minimum=15,Maximum=3600,Step=15,Format=ClawNumberFormat.Duration,Value=draft.DimAfterSeconds,AccessibleName=L.Text("idle_delay")};
        var brightness=new ClawSlider{Value=draft.DimBrightness};
        dim.Add(Row(560,(Column(Caption(L.Text("idle_delay_minutes_seconds")),delay,Note(L.Text("15_seconds_to_60_minutes"))),0),(Column(Caption(L.Text("dimmed_brightness")),brightness,Note(L.Text("touch_restores_full_brightness"))),0)));
        delay.ValueChanged+=(_,_)=>SetDraft(draft with{DimAfterSeconds=delay.Value});brightness.ValueChanged+=(_,_)=>SetDraft(draft with{DimBrightness=brightness.Value});
        void Mode(int value){for(int i=0;i<modes.Length;i++){modeButtons[i].Selected=modes[i].Item1==value;modeButtons[i].Invalidate();}delay.Enabled=brightness.Enabled=value!=0;
            behaviorNote.Text=value switch{1=>L.Text("dim_after_inactivity_when_your_pc_is_disconnected"),2=>L.Text("dim_after_inactivity_even_while_your_pc_is_connected"),_=>L.Text("keep_normal_brightness_night_hours_can_still_turn_the")};}
        for(int i=0;i<modes.Length;i++){int value=modes[i].Item1;modeButtons[i].Click+=(_,_)=>{SetDraft(draft with{DimBehavior=value});Mode(value);};}Mode(draft.DimBehavior);

        var night=p.Add(new ClawCard(L.Text("night_hours_card")));
        var start=new ClawTimeInput{Value=draft.NightStart,AccessibleName=L.Text("night_hours_start")};var end=new ClawTimeInput{Value=draft.NightEnd,AccessibleName=L.Text("night_hours_end")};
        night.Add(Switch(L.Text("turn_the_screen_off_during_night_hours"),draft.NightEnabled,value=>{SetDraft(draft with{NightEnabled=value});start.Enabled=end.Enabled=value;}));
        night.Add(Row(560,(Column(Caption(L.Text("from_24_hour_time")),start),0),(Column(Caption(L.Text("until_24_hour_time")),end),0)));
        start.Enabled=end.Enabled=draft.NightEnabled;
        start.ValueChanged+=(_,_)=>SetDraft(draft with{NightStart=start.Value});end.ValueChanged+=(_,_)=>SetDraft(draft with{NightEnd=end.Value});
        night.Add(Note(L.Text("uses_the_display_s_local_time_while_your_pc")));

        var offlineCard=p.Add(new ClawCard(L.Text("when_the_pc_has_been_offline_for_30_seconds")));
        var apps=new[]{new SettingChoice(5,L.Text("home"))}.Concat(DisplayApps.All.Where(a=>draft.Enabled(a.Id)&&a.Id!=8).Select(a=>new SettingChoice(a.Id,a.Name)));
        var offline=new ChoiceBox();foreach(var choice in apps)offline.Items.Add(choice);
        offline.SelectedIndex=offline.Items.Cast<SettingChoice>().ToList().FindIndex(c=>c.Value==(draft.Enabled(draft.OfflineApp)?draft.OfflineApp:5));
        offline.SelectedIndexChanged+=(_,_)=>{if(offline.SelectedItem is SettingChoice c)SetDraft(draft with{OfflineApp=c.Value});};
        offlineCard.Add(offline);offlineCard.Add(Note(L.Text("clock_is_the_default_a_running_timer_delays_automatic")));
    }
    private void PcPage()
    {
        title.Text=L.Text("a_quiet_companion_on_your_pc");subtitle.Text=L.Text("these_settings_apply_here_no_usb_cable_or_display");
        var p=Cards();
        var startCard=p.Add(new ClawCard(L.Text("startup_card")));
        startCard.Add(Switch(L.Text("start_clawpal_with_windows"),startup,value=>{startup=value;Dirty(false);}),2);
        startCard.Add(Note(L.Text("starts_quietly_in_the_tray_open_this_window_whenever")));
        var monitorCard=p.Add(new ClawCard(L.Text("pc_monitoring_card")));
        monitorCard.Add(Switch(L.Text("pause_pc_monitoring"),paused,value=>{paused=value;Dirty(false);}),2);
        monitorCard.Add(Note(L.Text("wireless_settings_remain_available_while_telemetry_is_paused")));
        monitorCard.Add(Note(L.Text("app_options_moved_note")));
        var help=p.Add(new ClawCard(L.Text("help_card")));
        var logs=new ClawButton(){Text=L.Text("open_logs"),Height=40};logs.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo("notepad.exe",'"'+Log.PathName+'"'){UseShellExecute=true});};
        var about=new ClawButton(){Text=L.Text("about_clawpal"),Height=40};about.Click+=(_,_)=>ShowPage(SettingsPage.About);
        help.Add(Row(0,(logs,160),(about,200)));
    }
    private void ConnectionPage()
    {
        title.Text=L.Text("right_here_over_wi_fi");subtitle.Text=L.Text("your_display_settings_travel_across_your_local_network");
        var p=Cards();var model=BoardCatalog.Get(config.BoardId);
        var info=p.Add(new ClawCard(model.ToString()));
        info.Add(Note(L.Text("device_version", preview?Installer.BundledVersion:wireless.FirmwareVersion.Length>0?wireless.FirmwareVersion:L.Text("waiting_for_display")),10),4);
        info.Add(Note(L.Text("wi_fi")+(preview?L.Text("home_wi_fi"):wireless.WifiSsid.Length>0?wireless.WifiSsid:L.Text("available_when_connected")),10),4);
        info.Add(Note(model.Description));

        var wifi=p.Add(new ClawCard(L.Text("change_wi_fi"),L.Text("the_display_must_be_online_it_reconnects_after_this")));
        var ssid=ClawTheme.Input(L.Text("new_2_4_ghz_wi_fi_name"));
        var password=ClawTheme.Input(L.Text("wi_fi_password_leave_blank_for_an_open_network"));password.UseSystemPasswordChar=true;
        var picker=wifi.Add(new WifiPicker(ssid,preview));
        wifi.Add(Row(560,(Column(Caption(L.Text("wi_fi_name_2_4_ghz")),ssid),0),(Column(Caption(L.Text("password_blank_for_an_open_network")),password),0)));
        var reveal=new ClawSwitch(){Text=L.Text("show_password"),Height=36};reveal.CheckedChanged+=(_,_)=>password.UseSystemPasswordChar=!reveal.Checked;wifi.Add(reveal);
        var change=new ClawButton(){Text=L.Text("change_network"),Primary=true,Height=42};wifi.Add(Row(0,(change,200)));
        change.Click+=async(_,_)=>{
            change.Enabled=false;crab.State="working";
            try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));await wireless.ChangeWifiAsync(ssid.Text,password.Text,message=>status.Text=message,timeout.Token);password.Clear();status.Text=L.Text("network_saved_the_display_is_reconnecting");crab.State="happy";}
            catch(Exception e){status.Text=e.Message;crab.State="error";}finally{if(!change.IsDisposed)change.Enabled=true;}
        };
        _=picker.LoadAsync();

        var maintenance=p.Add(new ClawCard(L.Text("maintenance"),L.Text("usb_is_for_initial_installation_and_recovery_updates_work")));
        var usb=new ClawButton(){Text=L.Text("usb_setup_recovery"),Height=40};usb.Click+=(_,_)=>OpenInstaller?.Invoke();
        maintenance.Add(Row(0,(usb,230)));
    }
    private async Task RefreshFirmware()
    {
        if(preview||connection.Updates.Busy)return;
        try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));await connection.Updates.ReadDevice(timeout.Token);}
        catch(Exception e){connection.Updates.ReportProblem(e);}
        if(!IsDisposed)RefreshState();
    }
    private void UpdatesPage()
    {
        title.Text=L.Text("a_little_better_over_wi_fi");subtitle.Text=L.Text("new_firmware_same_settings_clawpal_checks_releases_you_choose");
        var p=Cards();var updates=connection.Updates;
        var firmware=p.Add(new ClawCard(L.Text("display_firmware_card")));
        firmwareLabel=firmware.Add(ClawTheme.Label("",400,12,true,30),4);
        updateStatus=firmware.Add(Note("",10));
        installUpdate=new ClawButton(){Text=L.Text("install_over_wi_fi"),Primary=true,Height=42};
        prepareUpdate=new ClawButton(){Text=L.Text("prepare_over_usb_once"),Height=42};
        var refreshFirmware=new ClawButton(){Text=L.Text("refresh_display"),Height=42};
        refreshFirmware.Click+=async(_,_)=>await RefreshFirmware();
        firmware.Add(Row(0,(installUpdate,230),(prepareUpdate,230),(refreshFirmware,170)));
        firmware.Add(Note(L.Text("your_display_restarts_after_the_upload_keep_it_powered")));
        var releases=p.Add(new ClawCard(L.Text("github_releases"),L.Text("official_update_source")));
        var automatic=new ClawSwitch(){Text=L.Text("automatically_check_every_6_hours"),Checked=config.AutoCheckUpdates,Height=40};releases.Add(automatic);
        automatic.CheckedChanged+=(_,_)=>{if(!preview)updates.SetAutomaticChecks(automatic.Checked);};
        releaseStatus=releases.Add(Note("",10));
        checkUpdate=new ClawButton(){Text=L.Text("check_github_now"),Height=42};
        var pcUpdate=new ClawButton(){Text=L.Text("download_pc_update"),Height=42};
        pcUpdate.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo(updates.ReleasePage.AbsoluteUri){UseShellExecute=true});};
        releases.Add(Row(0,(checkUpdate,200),(pcUpdate,230)));
        releases.Add(Note(L.Text("official_update_help")));
        checkUpdate.Click+=async(_,_)=>{if(preview)return;try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));await updates.Check(timeout.Token);}catch(Exception e){updates.ReportProblem(e);status.Text=e.Message;crab.State="error";}RefreshState();};
        installUpdate.Click+=async(_,_)=>{
            if(preview)return;
            try{if(deviceDirty||pcDirty)await Save();if(deviceDirty||pcDirty)throw new IOException(L.Text("save_your_settings_before_updating_firmware"));
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));var task=updates.Install(updates.AvailableVersion is null,timeout.Token);RefreshState();await task;crab.State="happy";}
            catch(Exception e){updates.ReportProblem(e);status.Text=e.Message;crab.State="error";}finally{RefreshState();}
        };
        prepareUpdate.Click+=async(_,_)=>{
            if(preview)return;
            try{if(deviceDirty||pcDirty)await Save();if(deviceDirty||pcDirty)throw new IOException(L.Text("save_your_settings_before_preparing_wi_fi_updates"));
                var devices=await Task.Run(DeviceDiscovery.Find);var selected=DeviceDiscovery.Preferred(devices,null)??throw new IOException(L.Text("connect_only_your_paired_display_with_a_usb_c"));
                var progress=new Progress<string>(text=>{status.Text=text;if(updateStatus is not null)updateStatus.Text=text;});
                var task=updates.PrepareUsb(selected.Port,progress,CancellationToken.None);RefreshState();await task;crab.State="happy";}
            catch(Exception e){updates.ReportProblem(e);status.Text=e.Message;crab.State="error";}finally{RefreshState();}
        };
        RefreshState();
    }
    private async Task LoadDisplay()
    {
        try {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));var snapshot=await wireless.ReadAsync(timeout.Token);
            if(IsDisposed)return;
            if(!deviceDirty&&!config.SettingsPending){draft=snapshot.Settings;config.RememberDisplaySettings(draft);ClawTheme.Use(draft.Theme,draft.Accent);ShowPage(pageIndex);}
        }catch(NotSupportedException e){if(!IsDisposed){status.Text=e.Message;crab.State="error";}}
        catch(Exception){if(!IsDisposed)status.Text=L.Text("display_offline_you_can_save_changes_here_and_sync");}
    }
    private async Task Save()
    {
        if(saving||connection.Updates.Busy)return;saving=true;save.Enabled=false;crab.State="working";
        try {
            draft.Validate();shortcutDeck.Validate();
            if(preview){deviceDirty=pcDirty=shortcutsDirty=false;status.Text=L.Text("preview_only_no_settings_changed");return;}
            if(pcDirty){Startup.Set(startup);config.TrackFps=fps;config.AutoOpenApps=auto;config.Paused=paused;config.ClaudeLiveUsage=claudeLive;config.CompanionLanguage=pcLanguage;config.Save();L.SetLanguage(pcLanguage);connection.SetAutoOpenApps(auto);connection.SetPaused(paused);pcDirty=false;}
            if(shortcutsDirty){config.SetShortcuts(shortcutDeck);connection.ShortcutsChanged();shortcutsDirty=false;}
            if(deviceDirty){config.SetDisplaySettings(draft);deviceDirty=false;}
            if(config.SettingsPending){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(16));await wireless.SyncAsync(timeout.Token);}
            status.Text=L.Text("saved_your_desk_your_way");crab.State="happy";
        }catch(Exception e){status.Text=config.SettingsPending?L.Text("saved_on_this_pc")+DeviceSettingsClient.FailureMessage(e):e.Message;crab.State=e is ArgumentException?"error":"idle";}
        finally{saving=false;save.Enabled=deviceDirty||pcDirty;}
    }
    internal void ShowUpdates(){ShowPage(SettingsPage.Updates);Show();Activate();}
    internal void ReportProblem(string message){status.Text=message;crab.State="error";Show();Activate();}
    internal void RenderPreviews(string directory)
    {
        Directory.CreateDirectory(directory);Show();Application.DoEvents();
        void Capture(string name){Application.DoEvents();using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,name+".png"),ImageFormat.Png);}
        for(int i=0;i<=(int)SettingsPage.About;i++){ShowPage((SettingsPage)i);Capture($"settings-{i}");if(pages.MaximumOffset>0){pages.ScrollTo(pages.MaximumOffset);Capture($"settings-{i}-end");}}
        foreach(var size in new[]{new Size(960,680),new Size(1600,900)}){ClientSize=size;ShowPage(SettingsPage.Home);Capture($"home-{size.Width}");}
        WindowState=FormWindowState.Maximized;ShowPage(SettingsPage.Home);Capture("home-maximized");WindowState=FormWindowState.Normal;ClientSize=new(1120,790);
        foreach(var size in new[]{new Size(960,680),new Size(1120,790)}){ClientSize=size;ShowPage(SettingsPage.Apps);Application.DoEvents();pages.ScrollTo(pages.MaximumOffset);Capture($"claude-{size.Width}");}
        ClientSize=new(960,680);ShowPage(SettingsPage.Shortcuts);Capture("shortcuts-960");ShowPage(SettingsPage.Screen);Capture("screen-960");ClientSize=new(1120,790);
        ShowPage(SettingsPage.Clock);Application.DoEvents();var zones=activePage!.Controls.Cast<Control>().SelectMany(Descendants).OfType<ChoiceBox>().Last();zones.PerformClick();Application.DoEvents();
        var list=zones.SelectionList;using(var bitmap=new Bitmap(list.Width,list.Height)){list.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(directory,"choice-popup.png"),ImageFormat.Png);}zones.SelectionMenu.Close();
        Close();
    }
    private static IEnumerable<Control> Descendants(Control control){yield return control;foreach(Control child in control.Controls)foreach(var nested in Descendants(child))yield return nested;}
    protected override void Dispose(bool disposing){if(disposing){L.Changed-=LanguageChanged;ClawTheme.Changed-=LookChanged;refresh.Dispose();}base.Dispose(disposing);}

    /* A sidebar entry: left-aligned, with an accent bar on the page you are on. */
    private sealed class NavButton : ClawButton
    {
        internal NavButton(){Font=ClawTheme.Font(10,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(Parent?.BackColor??ClawTheme.Surface);
            bool hot=ClientRectangle.Contains(PointToClient(MousePosition));
            if(Selected||hot)ClawTheme.Fill(g,ClawTheme.Hover,new RectangleF(0,1,Width-1,Height-3),8);
            if(Selected)ClawTheme.Fill(g,ClawTheme.Accent,new RectangleF(0,9,3,Height-19),2);
            ClawTheme.Text(g,Text,Font,Selected?ClawTheme.Accent:ClawTheme.Ink,new Rectangle(16,0,Width-24,Height));
            if(Focused&&ShowFocusCues){using var p=ClawTheme.Round(new RectangleF(1,2,Width-3,Height-5),7);using var pen=new Pen(ClawTheme.Accent);g.DrawPath(pen,p);}
        }
    }
    /* An app's icon, name and description, with the switch that turns it on or off. */
    private sealed class AppHeader : Panel
    {
        internal event Action<bool>? Toggled;
        internal ClawSwitch Toggle {get;}
        private readonly DisplayApp app;
        internal AppHeader(DisplayApp app,bool on)
        {
            this.app=app;Height=54;BackColor=ClawTheme.Surface;DoubleBuffered=true;
            Toggle=new ClawSwitch(){Text="",Checked=on,Size=new(56,40),AccessibleName=app.Name,Anchor=AnchorStyles.Top|AnchorStyles.Right};
            Toggle.CheckedChanged+=(_,_)=>Toggled?.Invoke(Toggle.Checked);Controls.Add(Toggle);
            Resize+=(_,_)=>Toggle.Location=new(Width-Toggle.Width,(Height-Toggle.Height)/2);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            AppArtwork.Draw(g,app.Id,new Rectangle(0,(Height-34)/2,34,34));
            using var name=ClawTheme.Font(12,true);using var detail=ClawTheme.Font(9);
            ClawTheme.Text(g,app.Name,name,ClawTheme.Ink,new Rectangle(50,4,Width-120,26));
            ClawTheme.Text(g,app.Description,detail,ClawTheme.Muted,new Rectangle(50,28,Width-120,22));
        }
    }
}
