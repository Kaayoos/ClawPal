using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClawPal.Companion;

internal sealed class SettingsForm : ClawWindow
{
    private readonly Configuration config;
    private readonly Connection connection;
    private readonly DeviceSettingsClient wireless;
    private DeviceSettings draft;
    private string pcLanguage;
    private bool fps,auto,startup,paused,claudeLive,deviceDirty,pcDirty,saving;
    private readonly bool preview;
    private readonly Panel sidebar=new(){Dock=DockStyle.Left,Width=210,BackColor=ClawTheme.Surface};
    private readonly ClawScrollPanel pages=new(){Dock=DockStyle.Fill,Padding=new Padding(30,12,28,20)};
    private readonly Label title=ClawTheme.Label("",700,25,true,46),subtitle=ClawTheme.Label("",700,10,false,38),status=ClawTheme.Label("",640,9,false,50),link=ClawTheme.Label("",184,9,false,44);
    private readonly ClawButton save=new(){Text=L.Text("save_changes"),Primary=true,Width=156};
    private readonly CrabView crab=new(){Size=new(144,144),State="idle"};
    private readonly List<ClawButton> navigation=new();
    private readonly System.Windows.Forms.Timer refresh=new(){Interval=1000};
    private Label? updateStatus,releaseStatus,firmwareLabel;
    private ClawButton? installUpdate,prepareUpdate,checkUpdate;
    private Panel? activePage;
    private int pageIndex;
    private HomePreview? homePreview;
    private HomeOrderList? orderList;
    private ChoiceBox? addChoice;
    internal event Action? OpenInstaller;
    internal SettingsForm(Configuration config,Connection connection,bool preview=false)
    {
        this.config=config;this.connection=connection;pcLanguage=L.Normalize(config.CompanionLanguage);this.preview=preview;wireless=connection.Settings;
        draft=config.DisplaySettings??DeviceSettings.FromConfiguration(config);fps=config.TrackFps;auto=config.AutoOpenApps;paused=config.Paused;claudeLive=config.ClaudeLiveUsage;startup=!preview&&Startup.Enabled;
        Text=L.Text("clawpal_your_desk_companion");ClientSize=new(1120,790);AutoScaleMode=AutoScaleMode.Dpi;
        using(var stream=typeof(SettingsForm).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon"))if(stream is not null)Icon=new Icon(stream);
        var content=new Panel(){Dock=DockStyle.Fill};Controls.Add(content);Controls.Add(sidebar);
        var brand=ClawTheme.Label(L.Text("clawpal"),170,25,true,46);brand.Location=new(24,28);sidebar.Controls.Add(brand);
        link.Location=new(26,83);sidebar.Controls.Add(link);
        var names=new[]{L.Text("your_home"),L.Text("apps"),L.Text("clock_weather"),L.Text("pc_companion"),L.Text("device"),L.Text("updates"),L.Text("screen"),L.Text("language")};
        for(int i=0;i<names.Length;i++) {int index=i;var button=new ClawButton(){Text=names[i],Size=new(174,44),Location=new(18,146+i*44)};button.Click+=(_,_)=>ShowPage(index);navigation.Add(button);sidebar.Controls.Add(button);}
        sidebar.Controls.Add(crab);sidebar.Resize+=(_,_)=>{int size=Math.Min(144,Math.Max(0,sidebar.Height-565)/48*48);crab.Visible=size>=48;if(size>=48)crab.SetBounds((sidebar.Width-size)/2,sidebar.Height-size-64,size,size);};
        var version=ClawTheme.Label(L.Text("clawpal_94dbc7")+typeof(SettingsForm).Assembly.GetName().Version?.ToString(3),170,9,false,30);version.Location=new(25,sidebar.Height-48);version.Anchor=AnchorStyles.Left|AnchorStyles.Bottom;sidebar.Controls.Add(version);
        version.Cursor=Cursors.Hand;version.AccessibleName=L.Text("about_clawpal");
        version.Click+=(_,_)=>ShowPage(8);version.TabStop=true;
        version.KeyDown+=(_,e)=>{if(e.KeyCode is Keys.Enter or Keys.Space){ShowPage(8);e.Handled=true;}};
        var aboutHint=new ToolTip();aboutHint.SetToolTip(version,L.Text("about_clawpal"));Disposed+=(_,_)=>aboutHint.Dispose();
        var header=new Panel(){Dock=DockStyle.Top,Height=116,Padding=new Padding(30,27,28,0)};
        title.Dock=DockStyle.Top;subtitle.Dock=DockStyle.Fill;header.Controls.Add(subtitle);header.Controls.Add(title);
        var footer=new Panel(){Dock=DockStyle.Bottom,Height=96,Padding=new Padding(30,12,28,20)};
        save.Anchor=AnchorStyles.Right|AnchorStyles.Top;save.Top=20;status.Location=new(30,15);footer.Controls.AddRange([status,save]);
        footer.Resize+=(_,_)=>{save.Left=footer.Width-save.Width-28;status.Width=Math.Max(180,save.Left-48);};
        content.Controls.Add(pages);content.Controls.Add(header);content.Controls.Add(footer);
        save.Click+=async(_,_)=>await Save();
        refresh.Tick+=(_,_)=>RefreshState();refresh.Start();
        Shown+=async(_,_)=>{if(!preview){await LoadDisplay();await RefreshFirmware();}};
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing && !preview){e.Cancel=true;Hide();}};
        L.Changed+=LanguageChanged;ShowPage(0);RefreshState();
    }
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
        navigation[5].Text=updates.NotificationVersion is null?L.Text("updates"):L.Text("updates_1");
        if(updates.Busy)crab.State="working";
        pages.Enabled=!updates.Busy;
        link.Text=updates.Busy?L.Text("updating_your_display"):config.Paused?L.Text("pc_monitoring_paused"):online?L.Text("connected_over_wi_fi"):L.Text("display_offline_settings_are_still_available");link.ForeColor=online?ClawTheme.Orange:ClawTheme.Muted;
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
        draft=value;Dirty(true);homePreview?.SetSettings(draft);orderList?.SetSettings(draft);RefreshAddChoices();
    }
    private Panel Page(int height=510)
    {
        homePreview=null;orderList=null;addChoice=null;updateStatus=null;releaseStatus=null;firmwareLabel=null;installUpdate=null;prepareUpdate=null;checkUpdate=null;
        foreach(Control child in pages.Content.Controls.Cast<Control>().ToArray())child.Dispose();
        var p=new Panel(){Dock=DockStyle.Top,Width=pages.Content.Width,Height=height,BackColor=ClawTheme.Background};
        pages.Content.Controls.Add(p);activePage=p;pages.ResetScroll();
        return p;
    }
    private static Label Put(Panel panel,string text,int x,int y,int width,float size=10,bool bold=false,int height=32)
    {
        var label=ClawTheme.Label(text,width,size,bold,height);label.Location=new(x,y);panel.Controls.Add(label);return label;
    }
    private static ClawSwitch Switch(Panel panel,string text,bool value,int y,Action<bool> change)
    {
        var control=new ClawSwitch(){Text=text,Checked=value,Location=new(0,y),Width=panel.Width-12,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
        control.CheckedChanged+=(_,_)=>change(control.Checked);panel.Controls.Add(control);return control;
    }
    private void ShowPage(int index)
    {
        pageIndex=index;for(int i=0;i<navigation.Count;i++){navigation[i].Selected=i==index;navigation[i].Invalidate();}
        if(index==0)HomePage();else if(index==1)AppsPage();else if(index==2)ClockPage();else if(index==3)CompanionPage();else if(index==4)DevicePage();else if(index==5)UpdatesPage();else if(index==6)ScreenPage();else if(index==7)LanguagePage();else AboutPage();
    }

    private void LanguageChanged(){if(IsDisposed)return;pcLanguage=L.Language;L.RefreshControls(this);ShowPage(pageIndex);RefreshState();}
    private void AboutPage()
    {
        title.Text=L.Text("about_clawpal");subtitle.Text=L.Text("created_by_minimalpulse");var p=Page(670);
        void Link(string caption,string url,int y){var b=new ClawButton{Text=caption,Location=new(0,y),Width=Math.Min(510,p.Width),Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
            b.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo(url){UseShellExecute=true});};p.Controls.Add(b);}
        Put(p,L.Text("companion_version_value",typeof(SettingsForm).Assembly.GetName().Version?.ToString(3)),0,0,p.Width,13,true);
        Link("minimalpulse.com","https://minimalpulse.com/",48);
        Link("github.com/Kaayoos/clawpal","https://github.com/Kaayoos/clawpal",99);
        Put(p,L.Text("about_license"),0,165,p.Width,11,false,72);
        Put(p,L.Text("about_weather"),0,248,p.Width,10,false,76);
        Put(p,L.Text("about_tools"),0,334,p.Width,10,false,72);
        Put(p,L.Text("about_marks"),0,415,p.Width,10,false,76);
        var licenses=new ClawButton{Text=L.Text("open_licenses"),Location=new(0,515),Width=230};
        licenses.Click+=(_,_)=>{if(preview)return;Installer.PreparePayload();Process.Start(new ProcessStartInfo(Path.Combine(Installer.PayloadPath,"licenses","THIRD_PARTY_NOTICES.txt")){UseShellExecute=true});};p.Controls.Add(licenses);
        Put(p,L.Text("about_device_hint"),0,580,p.Width,10,false,70);
    }

    private void LanguagePage()
    {
        title.Text=L.Text("language_title");subtitle.Text=L.Text("language_description");var p=Page(380);
        ChoiceBox Picker(string title,int y,string code,Action<string> change){
            Put(p,title,0,y,p.Width,12,true);var picker=new ChoiceBox{Location=new(0,y+42),Width=Math.Min(500,p.Width),Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
            foreach(var language in L.Languages)picker.Items.Add(language);
            picker.SelectedIndex=L.Languages.ToList().FindIndex(l=>l.Code==code);
            picker.SelectedIndexChanged+=(_,_)=>{if(picker.SelectedItem is LanguageChoice language)change(language.Code);};p.Controls.Add(picker);return picker;
        }
        Picker(L.Text("pc_language"),0,pcLanguage,code=>{pcLanguage=code;Dirty(false);});
        Picker(L.Text("display_language"),133,draft.Language,code=>SetDraft(draft with{Language=code}));
        Put(p,L.Text("language_note"),0,272,p.Width,10,false,80);
    }

    private void HomePage()
    {
        title.Text=L.Text("a_place_for_your_favorites");subtitle.Text=L.Text("arrange_your_shortcuts_your_crab_keeps_the_best_seat");
        var p=Page();homePreview=new(draft,config.BoardId);orderList=new(draft){Font=ClawTheme.Font()};p.Controls.AddRange([homePreview,orderList]);
        var previewCaption=Put(p,L.Text("your_display_dimensions",BoardCatalog.Get(config.BoardId).Width,BoardCatalog.Get(config.BoardId).Height),0,0,390,9);
        var listCaption=Put(p,L.Text("on_your_home"),0,0,300,13,true);
        var instruction=Put(p,L.Text("drag_to_reorder_the_minus_removes_a_shortcut"),0,0,330,9,false,38);
        var hint=Put(p,L.Text("apps_stay_available_in_the_display_s_apps_menu"),0,0,395,10,false,65);
        addChoice=new ChoiceBox(){Width=220,Height=38};var add=new ClawButton(){Text=L.Text("add"),Width=64,Height=38};p.Controls.AddRange([addChoice,add]);
        add.Click+=(_,_)=>{if(draft.HomeOrder.Length<5 && addChoice.SelectedItem is DisplayAppChoice choice)SetDraft(draft with{HomeOrder=draft.HomeOrder.Append(choice.App.Id).ToArray()});};
        orderList.Changed+=SetDraft;
        void Layout(){
            if(homePreview is null||orderList is null||addChoice is null)return;
            bool sideBySide=p.Width>=790;
            int left=sideBySide?Math.Min(700,(int)(p.Width*.59)):Math.Min(560,p.Width);
            var model=BoardCatalog.Get(config.BoardId);homePreview.SetBounds(0,28,left,left*model.Height/model.Width);previewCaption.SetBounds(0,0,left,24);
            hint.SetBounds(0,homePreview.Bottom+18,left,58);
            int x=sideBySide?left+28:0,y=sideBySide?0:hint.Bottom+24,right=sideBySide?p.Width-x:left;
            listCaption.SetBounds(x,y,right,30);instruction.SetBounds(x,y+34,right,40);
            orderList.SetBounds(x,y+80,right,310);addChoice.SetBounds(x,y+410,Math.Max(90,right-76),44);add.SetBounds(x+right-64,y+410,64,44);
            p.Height=Math.Max(hint.Bottom,y+454)+24;
        }
        p.Resize+=(_,_)=>Layout();Layout();RefreshAddChoices();
    }
    private sealed record DisplayAppChoice(DisplayApp App){public override string ToString()=>App.Name;}
    private void RefreshAddChoices()
    {
        if(addChoice is null)return;addChoice.Items.Clear();if(draft.HomeOrder.Length>=5){addChoice.SelectedIndex=-1;addChoice.Enabled=false;addChoice.Text=L.Text("home_holds_up_to_5_shortcuts");return;}foreach(var app in DisplayApps.All.Where(a=>draft.Enabled(a.Id)&&!draft.HomeOrder.Contains(a.Id)))addChoice.Items.Add(new DisplayAppChoice(app));
        addChoice.SelectedIndex=addChoice.Items.Count>0?0:-1;addChoice.Enabled=addChoice.Items.Count>0;
        if(addChoice.Items.Count==0)addChoice.Text=L.Text("all_enabled_apps_are_on_home");
    }
    private void AppsPage()
    {
        title.Text=L.Text("a_little_less_or_all_of_it");subtitle.Text=L.Text("choose_the_apps_available_on_your_display_home_stays");
        var p=Page(DisplayApps.All.Length*91+65);
        int row=0;foreach(var app in DisplayApps.All){int y=row++*91;var icon=new AppIcon(app.Id){Location=new(1,y+8),Size=new(36,36)};p.Controls.Add(icon);
            var toggle=new ClawSwitch(){Text=app.Name,Checked=draft.Enabled(app.Id),Location=new(53,y),Width=p.Width-65,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
            toggle.CheckedChanged+=(_,_)=>SetDraft(draft.SetEnabled(app.Id,toggle.Checked));p.Controls.Add(toggle);Put(p,app.Description,53,y+42,p.Width-65,9);
        }
        Put(p,L.Text("turning_an_app_off_also_removes_its_home_shortcut"),0,DisplayApps.All.Length*91+8,p.Width,9,false,42);
    }
    private void ClockPage()
    {
        title.Text=L.Text("your_time_your_little_forecast");subtitle.Text=L.Text("time_and_weather_are_saved_on_the_display_and");
        var p=Page(670);
        Put(p,L.Text("time_format"),0,0,300,12,true);
        var twentyFour=new ClawButton(){Text=L.Text("24_hours_17_34"),Location=new(0,40),Width=195,Selected=draft.Use24h};
        var twelve=new ClawButton(){Text=L.Text("12_hours_5_34_pm"),Location=new(207,40),Width=205,Selected=!draft.Use24h};p.Controls.AddRange([twentyFour,twelve]);
        void Hours(bool value){SetDraft(draft with{Use24h=value});twentyFour.Selected=value;twelve.Selected=!value;twentyFour.Invalidate();twelve.Invalidate();}
        twentyFour.Click+=(_,_)=>Hours(true);twelve.Click+=(_,_)=>Hours(false);
        Switch(p,L.Text("show_minimal_weather_in_clock"),draft.WeatherEnabled,108,value=>SetDraft(draft with{WeatherEnabled=value}));
        Put(p,L.Text("weather_location"),0,178,400,12,true);
        var city=ClawTheme.Input(L.Text("find_a_city_or_village"));city.Text=draft.City=="Weather"?"":draft.City;city.SetBounds(0,216,p.Width-167,44);city.Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right;
        var search=new ClawButton(){Text=L.Text("find_location"),Location=new(p.Width-155,216),Width=155,Anchor=AnchorStyles.Right|AnchorStyles.Top};
        var zones=new ChoiceBox(){Location=new(0,401),Width=p.Width,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
        var choices=new ChoiceBox(){Location=new(0,274),Width=p.Width,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
        choices.Items.Add(new Place(draft.City,"","",draft.Latitude,draft.Longitude,""));choices.SelectedIndex=0;
        var feedback=Put(p,L.Text("search_then_choose_the_location_that_matches"),0,325,p.Width,9,false,35);
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
                feedback.Text=choices.Items.Count>0?L.Text("choose_your_location_from_the_results"):L.Text("no_match_try_a_nearby_town");crab.State=choices.Items.Count>0?"idle":"error";
            }catch(Exception){if(!feedback.IsDisposed)feedback.Text=L.Text("location_search_is_unavailable_check_your_internet_connection_and");crab.State="error";}
            finally{if(!search.IsDisposed)search.Enabled=true;}
        };
        p.Controls.AddRange([city,search,choices]);
        Put(p,L.Text("time_zone"),0,361,300,12,true);
        TimeZones.Fill(zones,draft.Timezone);
        zones.SelectedIndexChanged+=(_,_)=>{if(zones.SelectedItem is TimeZoneChoice zone)SetDraft(draft with{Timezone=zone.Posix});};p.Controls.Add(zones);
        Put(p,L.Text("weather_temperature_unit"),0,469,p.Width,12,true);
        var celsius=new ClawButton{Text=L.Text("celsius"),Location=new(0,508),Width=195,Selected=!draft.WeatherFahrenheit};
        var fahrenheit=new ClawButton{Text=L.Text("fahrenheit"),Location=new(207,508),Width=205,Selected=draft.WeatherFahrenheit};
        void Units(bool useFahrenheit){SetDraft(draft with{WeatherFahrenheit=useFahrenheit});celsius.Selected=!useFahrenheit;fahrenheit.Selected=useFahrenheit;celsius.Invalidate();fahrenheit.Invalidate();}
        celsius.Click+=(_,_)=>Units(false);fahrenheit.Click+=(_,_)=>Units(true);p.Controls.AddRange([celsius,fahrenheit]);
        Put(p,L.Text("weather_unit_scope"),0,562,p.Width,9,false,36);
        Put(p,L.Text("weather_by_open_meteo_location_search_by_geonames"),0,620,p.Width,9,false,27);
    }
    private sealed record SettingChoice(int Value,string Label){public override string ToString()=>Label;}
    private ChoiceBox Choice(Panel panel,string label,int y,int value,IEnumerable<SettingChoice> choices,Action<int> change,int x=0,int width=360)
    {
        Put(panel,label,x,y,width,10,true);
        var control=new ChoiceBox(){Location=new(x,y+34),Width=width};
        foreach(var choice in choices)control.Items.Add(choice);
        control.SelectedIndex=control.Items.Cast<SettingChoice>().ToList().FindIndex(c=>c.Value==value);
        control.SelectedIndexChanged+=(_,_)=>{if(control.SelectedItem is SettingChoice c)change(c.Value);};panel.Controls.Add(control);return control;
    }
    private void ScreenPage()
    {
        title.Text=L.Text("a_screen_that_settles_with_you");subtitle.Text=L.Text("touch_to_wake_these_preferences_work_on_every_app");
        var p=Page(706);
        Put(p,L.Text("automatic_dimming"),0,0,p.Width,12,true);
        var modes=new[]{(1,L.Text("pc_offline")),(2,L.Text("whenever_idle")),(0,L.Text("never"))};
        var modeButtons=modes.Select(mode=>new ClawButton{Text=mode.Item2,Selected=draft.DimBehavior==mode.Item1,AccessibleName=L.Text("dim")+mode.Item2}).ToArray();
        p.Controls.AddRange(modeButtons);
        var behaviorNote=Put(p,"",0,90,p.Width,9,false,38);
        var delayLabel=Put(p,L.Text("idle_delay_minutes_seconds"),0,148,300,10,true);
        var delay=new ClawTimeInput{Minimum=15,Maximum=3600,Step=15,Format=ClawNumberFormat.Duration,Value=draft.DimAfterSeconds,AccessibleName=L.Text("idle_delay")};
        var brightnessLabel=Put(p,L.Text("dimmed_brightness"),0,148,360,10,true);
        var brightness=new ClawSlider{Value=draft.DimBrightness};p.Controls.AddRange([delay,brightness]);
        var brightnessNote=Put(p,L.Text("touch_restores_full_brightness"),0,237,p.Width,9,false,28);
        var delayNote=Put(p,L.Text("15_seconds_to_60_minutes"),0,237,p.Width,9,false,28);
        delay.ValueChanged+=(_,_)=>SetDraft(draft with{DimAfterSeconds=delay.Value});brightness.ValueChanged+=(_,_)=>SetDraft(draft with{DimBrightness=brightness.Value});
        void Mode(int value){for(int i=0;i<modes.Length;i++){modeButtons[i].Selected=modes[i].Item1==value;modeButtons[i].Invalidate();}delay.Enabled=brightness.Enabled=value!=0;
            behaviorNote.Text=value switch{1=>L.Text("dim_after_inactivity_when_your_pc_is_disconnected"),2=>L.Text("dim_after_inactivity_even_while_your_pc_is_connected"),_=>L.Text("keep_normal_brightness_night_hours_can_still_turn_the")};}
        for(int i=0;i<modes.Length;i++){int value=modes[i].Item1;modeButtons[i].Click+=(_,_)=>{SetDraft(draft with{DimBehavior=value});Mode(value);};}Mode(draft.DimBehavior);
        var start=new ClawTimeInput{Value=draft.NightStart,AccessibleName=L.Text("night_hours_start")};var end=new ClawTimeInput{Value=draft.NightEnd,AccessibleName=L.Text("night_hours_end")};
        Switch(p,L.Text("turn_the_screen_off_during_night_hours"),draft.NightEnabled,289,value=>{SetDraft(draft with{NightEnabled=value});start.Enabled=end.Enabled=value;});
        var from=Put(p,L.Text("from_24_hour_time"),0,350,300,10,true);var until=Put(p,L.Text("until_24_hour_time"),0,350,300,10,true);
        p.Controls.AddRange([start,end]);start.Enabled=end.Enabled=draft.NightEnabled;
        start.ValueChanged+=(_,_)=>SetDraft(draft with{NightStart=start.Value});end.ValueChanged+=(_,_)=>SetDraft(draft with{NightEnd=end.Value});
        var nightNote=Put(p,L.Text("uses_the_display_s_local_time_while_your_pc"),0,451,p.Width,9,false,64);
        var apps=new[]{new SettingChoice(5,L.Text("home"))}.Concat(DisplayApps.All.Where(a=>draft.Enabled(a.Id)).Select(a=>new SettingChoice(a.Id,a.Name)));
        var offline=Choice(p,L.Text("when_the_pc_has_been_offline_for_30_seconds"),544,draft.Enabled(draft.OfflineApp)?draft.OfflineApp:5,apps,value=>SetDraft(draft with{OfflineApp=value}),width:p.Width);
        var offlineNote=Put(p,L.Text("clock_is_the_default_a_running_timer_delays_automatic"),0,636,p.Width,9,false,58);
        void LayoutScreen(){int half=(p.Width-28)/2;
            for(int i=0;i<modeButtons.Length;i++)modeButtons[i].SetBounds(i*(p.Width+8)/3,38,(p.Width-16)/3,44);
            behaviorNote.Width=p.Width;delayLabel.Width=half;delay.SetBounds(0,187,half,48);
            brightnessLabel.SetBounds(half+28,148,half,32);brightness.SetBounds(half+28,187,half,48);
            from.Width=half;start.SetBounds(0,389,half,48);until.SetBounds(half+28,350,half,32);end.SetBounds(half+28,389,half,48);
            delayNote.Width=half;brightnessNote.SetBounds(half+28,237,half,28);nightNote.Width=offlineNote.Width=offline.Width=p.Width;
        }
        p.Resize+=(_,_)=>LayoutScreen();LayoutScreen();
    }
    private void CompanionPage()
    {
        title.Text=L.Text("a_quiet_companion_on_your_pc");subtitle.Text=L.Text("these_settings_apply_here_no_usb_cable_or_display");
        var p=Page(820);
        Switch(p,L.Text("start_clawpal_with_windows"),startup,0,value=>{startup=value;Dirty(false);});Put(p,L.Text("starts_quietly_in_the_tray_open_this_window_whenever"),0,46,p.Width,9);
        Switch(p,L.Text("track_game_fps_in_pc_monitor"),fps,108,value=>{fps=value;Dirty(false);});Put(p,L.Text("uses_windows_frame_events_through_presentmon_game_files_stay"),0,154,p.Width,9,false,40);
        Switch(p,L.Text("automatically_open_spotify_or_codex"),auto,216,value=>{auto=value;Dirty(false);});Put(p,L.Text("when_an_enabled_app_opens_on_your_pc_show"),0,262,p.Width,9);
        Put(p,L.Text("claude_link_title"),0,440,p.Width,12,true);
        Put(p,L.Text("claude_link_description"),0,478,p.Width,9,false,62);
        var claudeStatus=Put(p,preview?L.Text("claude_link_ready"):ClaudeBridge.Status,0,592,p.Width,9,false,55);
        var claudeLink=new ClawButton(){Text=L.Text("claude_link_button"),Location=new(0,546),Width=245};p.Controls.Add(claudeLink);
        claudeLink.Click+=(_,_)=>{try{if(!preview)ClaudeBridge.Install();claudeStatus.Text=L.Text("claude_link_ready");crab.State="happy";}catch(Exception error){claudeStatus.Text=error.Message;crab.State="error";}};
        var claudeUnlink=new ClawButton(){Text=L.Text("claude_link_disconnect"),Location=new(257,546),Width=230};p.Controls.Add(claudeUnlink);
        var liveSwitch=Switch(p,L.Text("claude_live_limits"),claudeLive,666,value=>{claudeLive=value;Dirty(false);});
        Put(p,L.Text("claude_live_description"),0,712,p.Width,9,false,96);
        claudeUnlink.Click+=(_,_)=>{try{if(!preview){ClaudeBridge.Uninstall();config.ClaudeLiveUsage=false;config.Save();}liveSwitch.Checked=false;claudeLive=false;claudeStatus.Text=L.Text("claude_link_removed");crab.State="idle";}catch(Exception error){claudeStatus.Text=error.Message;crab.State="error";}};
        Switch(p,L.Text("pause_pc_monitoring"),paused,324,value=>{paused=value;Dirty(false);});Put(p,L.Text("wireless_settings_remain_available_while_telemetry_is_paused"),0,370,p.Width,9);
    }
    private void DevicePage()
    {
        title.Text=L.Text("right_here_over_wi_fi");subtitle.Text=L.Text("your_display_settings_travel_across_your_local_network");
        var p=Page(670);
        Put(p,BoardCatalog.Get(config.BoardId).ToString(),0,0,p.Width,14,true,38);
        Put(p,BoardCatalog.Get(config.BoardId).Description,0,112,p.Width,9,false,28);
        Put(p,L.Text("device_version", preview?Installer.BundledVersion:wireless.FirmwareVersion.Length>0?wireless.FirmwareVersion:L.Text("waiting_for_display")),0,44,p.Width,10);
        Put(p,L.Text("wi_fi")+(preview?L.Text("home_wi_fi"):wireless.WifiSsid.Length>0?wireless.WifiSsid:L.Text("available_when_connected")),0,81,p.Width,10);
        Put(p,L.Text("change_wi_fi"),0,145,p.Width,12,true);
        int half=(p.Width-18)/2;
        var ssidLabel=Put(p,L.Text("wi_fi_name_2_4_ghz"),0,185,half,9);var passwordLabel=Put(p,L.Text("password_blank_for_an_open_network"),half+18,185,half,9);
        var ssid=ClawTheme.Input(L.Text("new_2_4_ghz_wi_fi_name"));ssid.SetBounds(0,219,half,44);ssid.Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right;
        var password=ClawTheme.Input(L.Text("wi_fi_password_leave_blank_for_an_open_network"));password.UseSystemPasswordChar=true;password.SetBounds(half+18,219,half,44);password.Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right;p.Controls.AddRange([ssid,password]);
        void LayoutWifi(){int width=(p.Width-18)/2;ssidLabel.SetBounds(0,185,width,32);passwordLabel.SetBounds(width+18,185,width,32);ssid.SetBounds(0,219,width,44);password.SetBounds(width+18,219,width,44);}
        p.Resize+=(_,_)=>LayoutWifi();LayoutWifi();
        var change=new ClawButton(){Text=L.Text("change_network"),Location=new(0,310),Width=180};p.Controls.Add(change);
        Put(p,L.Text("the_display_must_be_online_it_reconnects_after_this"),196,307,p.Width-196,9,false,57);
        var reveal=new ClawSwitch(){Text=L.Text("show_password"),Width=250,Location=new(0,268),Height=34};reveal.CheckedChanged+=(_,_)=>password.UseSystemPasswordChar=!reveal.Checked;p.Controls.Add(reveal);
        change.Click+=async(_,_)=>{
            change.Enabled=false;crab.State="working";
            try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));await wireless.ChangeWifiAsync(ssid.Text,password.Text,message=>status.Text=message,timeout.Token);password.Clear();status.Text=L.Text("network_saved_the_display_is_reconnecting");crab.State="happy";}
            catch(Exception e){status.Text=e.Message;crab.State="error";}finally{if(!change.IsDisposed)change.Enabled=true;}
        };
        Put(p,L.Text("maintenance"),0,397,p.Width,12,true);
        var usb=new ClawButton(){Text=L.Text("usb_setup_recovery"),Location=new(0,440),Width=210};usb.Click+=(_,_)=>OpenInstaller?.Invoke();p.Controls.Add(usb);
        var logs=new ClawButton(){Text=L.Text("open_logs"),Location=new(222,440),Width=130};logs.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo("notepad.exe",'"'+Log.PathName+'"'){UseShellExecute=true});};p.Controls.Add(logs);
        Put(p,L.Text("usb_is_for_initial_installation_and_recovery_updates_work"),0,496,p.Width,9,false,42);
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
        var p=Page(670);var updates=connection.Updates;
        firmwareLabel=Put(p,"",0,0,p.Width,13,true,34);
        updateStatus=Put(p,"",0,40,p.Width,10,false,54);
        installUpdate=new ClawButton(){Text=L.Text("install_over_wi_fi"),Primary=true,Location=new(0,104),Width=224};
        prepareUpdate=new ClawButton(){Text=L.Text("prepare_over_usb_once"),Location=new(0,104),Width=224};
        var refreshFirmware=new ClawButton(){Text=L.Text("refresh_display"),Location=new(236,104),Width=154};
        refreshFirmware.Click+=async(_,_)=>await RefreshFirmware();p.Controls.AddRange([installUpdate,prepareUpdate,refreshFirmware]);
        Put(p,L.Text("your_display_restarts_after_the_upload_keep_it_powered"),0,157,p.Width,9,false,45);
        Put(p,L.Text("github_releases"),0,215,p.Width,13,true,34);
        Put(p,L.Text("official_update_source"),0,255,p.Width,10,false,48);
        var automatic=new ClawSwitch(){Text=L.Text("automatically_check_every_6_hours"),Checked=config.AutoCheckUpdates,Location=new(0,311),Width=p.Width,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};p.Controls.Add(automatic);
        automatic.CheckedChanged+=(_,_)=>{if(!preview)updates.SetAutomaticChecks(automatic.Checked);};
        releaseStatus=Put(p,"",0,364,p.Width,10,false,60);
        checkUpdate=new ClawButton(){Text=L.Text("check_github_now"),Location=new(0,438),Width=196};p.Controls.Add(checkUpdate);
        var pcUpdate=new ClawButton(){Text=L.Text("download_pc_update"),Location=new(208,438),Width=230};p.Controls.Add(pcUpdate);
        pcUpdate.Click+=(_,_)=>{if(!preview)Process.Start(new ProcessStartInfo(updates.ReleasePage.AbsoluteUri){UseShellExecute=true});};
        Put(p,L.Text("official_update_help"),0,494,p.Width,9,false,70);
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
            if(!deviceDirty&&!config.SettingsPending){draft=snapshot.Settings;config.RememberDisplaySettings(draft);ShowPage(pageIndex);}
        }catch(NotSupportedException e){if(!IsDisposed){status.Text=e.Message;crab.State="error";}}
        catch(Exception){if(!IsDisposed)status.Text=L.Text("display_offline_you_can_save_changes_here_and_sync");}
    }
    private async Task Save()
    {
        if(saving||connection.Updates.Busy)return;saving=true;save.Enabled=false;crab.State="working";
        try {
            draft.Validate();
            if(preview){deviceDirty=pcDirty=false;status.Text=L.Text("preview_only_no_settings_changed");return;}
            if(pcDirty){Startup.Set(startup);config.TrackFps=fps;config.AutoOpenApps=auto;config.Paused=paused;config.ClaudeLiveUsage=claudeLive;config.CompanionLanguage=pcLanguage;config.Save();L.SetLanguage(pcLanguage);connection.SetAutoOpenApps(auto);connection.SetPaused(paused);pcDirty=false;}
            if(deviceDirty){config.SetDisplaySettings(draft);deviceDirty=false;}
            if(config.SettingsPending){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(16));await wireless.SyncAsync(timeout.Token);}
            status.Text=L.Text("saved_your_desk_your_way");crab.State="happy";
        }catch(Exception e){status.Text=config.SettingsPending?L.Text("saved_on_this_pc")+DeviceSettingsClient.FailureMessage(e):e.Message;crab.State=e is ArgumentException?"error":"idle";}
        finally{saving=false;save.Enabled=deviceDirty||pcDirty;}
    }
    internal void ShowUpdates(){ShowPage(5);Show();Activate();}
    internal void ReportProblem(string message){status.Text=message;crab.State="error";Show();Activate();}
    internal void RenderPreviews(string directory)
    {
        Directory.CreateDirectory(directory);Show();Application.DoEvents();
        for(int i=0;i<9;i++){ShowPage(i);Application.DoEvents();using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,$"settings-{i}.png"),ImageFormat.Png);}
        void Capture(string name){Application.DoEvents();using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(directory,name+".png"),ImageFormat.Png);}
        foreach(var size in new[]{new Size(960,680),new Size(1600,900)}){ClientSize=size;ShowPage(0);Capture($"home-{size.Width}");}
        WindowState=FormWindowState.Maximized;ShowPage(0);Capture("home-maximized");WindowState=FormWindowState.Normal;ClientSize=new(1120,790);
        foreach(var size in new[]{new Size(960,680),new Size(1120,790)}){ClientSize=size;ShowPage(3);Application.DoEvents();pages.ScrollTo(pages.MaximumOffset);Capture($"claude-{size.Width}");}
        ShowPage(6);pages.ScrollTo(pages.MaximumOffset);Capture("screen-bottom");
        ShowPage(2);pages.ScrollTo(pages.MaximumOffset);Capture("weather-units");
        ShowPage(2);Application.DoEvents();var choices=activePage!.Controls.OfType<ChoiceBox>().Last();choices.PerformClick();Application.DoEvents();
        using(var bitmap=new Bitmap(choices.SelectionMenu.Width,choices.SelectionMenu.Height)){choices.SelectionMenu.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(directory,"choice-popup.png"),ImageFormat.Png);}choices.SelectionMenu.Close();
        Close();
    }
    protected override void Dispose(bool disposing){if(disposing){L.Changed-=LanguageChanged;refresh.Dispose();}base.Dispose(disposing);}
    private sealed class AppIcon(int id):Control {protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;AppArtwork.Draw(e.Graphics,id,ClientRectangle);}}
}

