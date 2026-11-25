using ClawPal.Companion;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

internal static class SettingsChecks
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle,uint message,IntPtr w,IntPtr l);
    internal static int Run()
    {
        int passed=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS: "+name);}
        void Reject(DeviceSettings value,string name){bool rejected=false;try{value.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,name);}
        var settings=new DeviceSettings{City="Warsaw",Latitude=52.2297,Longitude=21.0122,Timezone="CET-1CEST,M3.5.0,M10.5.0/3"};settings.Validate();
        Check(settings.HomeOrder.SequenceEqual(new[]{0,4,7,2,1}),"new installations include Claude in Home");
        Check(settings.Move(0,2).HomeOrder.SequenceEqual(new[]{4,7,0,2,1}) && settings.HomeOrder[0]==0,"reordering preserves immutable original");
        Check(settings.SetEnabled(2,false).HomeOrder.SequenceEqual(new[]{0,4,7,1}),"disabling an app removes its Home shortcut");
        Check(settings.SetEnabled(2,false).SetEnabled(2,true).Enabled(2),"app can be enabled again");
        (settings with{EnabledApps=32,HomeOrder=[]}).Validate();Check(true,"Home alone remains a valid device configuration");
        Reject(settings with{EnabledApps=31},"Home cannot be disabled");Reject(settings with{EnabledApps=1056},"unknown app bits rejected");
        Reject(settings with{HomeOrder=[0,0]},"duplicate shortcuts rejected");Reject(settings with{HomeOrder=[5]},"Home cannot link recursively to itself");
        Reject(settings with{EnabledApps=32,HomeOrder=[0]},"disabled shortcut rejected");Reject(settings with{Latitude=double.NaN},"invalid settings coordinate rejected");
        Reject(settings with{City="City\0suffix"},"embedded null in location rejected");
        string json=JsonSerializer.Serialize(settings with{Use24h=false},DeviceSettingsClient.Json);
        Check(json.Contains("\"use_24h\":false")&&json.Contains("\"home_order\""),"wireless settings use firmware JSON names");
        var parsed=DeviceSettingsClient.Parse(json);
        Check(!parsed.Settings.Use24h && parsed.Settings.HomeOrder.SequenceEqual(settings.HomeOrder),"wireless settings round-trip preserves format and order");
        var shown=DeviceSettingsClient.Parse(JsonSerializer.Serialize(settings with{ShowVersion=true},DeviceSettingsClient.Json));
        Check(json.Contains("\"show_version\":false")&&shown.Settings.ShowVersion&&!DeviceSettingsClient.Equivalent(shown.Settings,settings),"Home's version choice is sent to the display and noticed when it changes");
        var beforeAudio=DeviceSettingsClient.Parse(JsonSerializer.Serialize(settings with{Schema=8,EnabledApps=settings.EnabledApps&~512,ShowVersion=true},DeviceSettingsClient.Json).Replace("\"show_version\":true,",""));
        Check(beforeAudio.ProtocolSchema==8&&beforeAudio.Settings.Schema==DeviceSettings.CurrentSchema&&beforeAudio.Settings.Enabled(9)&&!beforeAudio.Settings.ShowVersion,"schema 8 displays gain Audio and keep Home's footer quiet");
        Reject(settings with{OfflineApp=9},"Audio cannot be the offline app");
        bool missing=false;try{DeviceSettingsClient.Parse("{\"schema\":2}");}catch(IOException){missing=true;}Check(missing,"incomplete device settings cannot replace local preferences");
        bool old=false;try{DeviceSettingsClient.Parse("{\"schema\":1}");}catch(NotSupportedException){old=true;}Check(old,"older firmware is identified before applying settings");
        Check(StartupContract.ShouldShowSettings(true,true),"paired completed installation opens settings even offline");
        Check(!StartupContract.ShouldShowSettings(false,true),"unfinished installation returns to setup");
        Check(!StartupContract.ShouldShowSettings(true,false),"missing pairing opens first-run setup");
        using(var body=DeviceSettingsClient.JsonBody(settings)) {
            var bytes=body.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Check(body.Headers.ContentLength==bytes.Length && bytes.Length>0,"ESP settings body has an explicit UTF-8 content length");
        }
        using(var body=DeviceSettingsClient.JsonBody(new {ssid="Wi-Fi Łódź",password="password123"}))
            Check(body.Headers.ContentLength==body.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length,"Wi-Fi change body has an explicit byte length");
        Check(DeviceSettingsClient.Equivalent(settings,settings with{Latitude=settings.Latitude+1e-12}),"firmware float formatting does not leave settings pending");
        // The display stores at most 63 city bytes; setup and later settings must agree on that limit.
        var setup=new SetupOptions("COM4","Test network","password123","x",52.1,20.9,"UTC0",false,false,false);
        foreach(int bytes in new[]{63,64,73}) {
            string city=new string('a',bytes);bool settingsOk=true,setupOk=true;
            try{(settings with{City=city}).Validate();}catch(ArgumentException){settingsOk=false;}
            try{(setup with{City=city}).Validate();}catch(ArgumentException){setupOk=false;}
            Check(settingsOk==setupOk && settingsOk==(bytes<=63),$"{bytes}-byte city handled like the firmware");
        }
        var bodies=new List<string>();
        string Confirm(params string[] replies){
            var handler=new ScriptedHandler(replies);int waits=0;
            using var client=new HttpClient(handler);
            try{DeviceSettingsClient.ConfirmOnDisplayAsync(client,new Uri("http://192.0.2.1:42171/firmware"),"firmware","0.10.2",()=>waits++,CancellationToken.None).GetAwaiter().GetResult();}
            catch(IOException){return "rejected:"+waits;}
            Check(handler.Paths.All(path=>path=="/confirm"),"confirmation polls the display's /confirm endpoint");
            bodies.AddRange(handler.Bodies);
            return "ok:"+waits;
        }
        Check(Confirm("approved")=="ok:0","approved change proceeds immediately");
        Check(Confirm("pending","approved")=="ok:1","pending change asks the user to tap the display once");
        Check(Confirm("pending","denied")=="rejected:1","declined change is not sent");
        Check(Confirm("404")=="ok:0","firmware without confirmation support keeps working");
        Check(bodies.Count>0&&bodies.All(body=>body=="{\"action\":\"firmware\",\"version\":\"0.10.2\"}"),"firmware approval names the version for the display's prompt");
        Check(!DeviceSettingsClient.Equivalent(settings,settings.Move(0,1)),"settings confirmation still checks shortcut order");
        Check(DeviceSettingsClient.FailureMessage(new HttpRequestException("rejected",null,System.Net.HttpStatusCode.BadRequest)).Contains("rejected"),"request rejection is not reported as an offline display");
        Check(settings.Enabled(6) && DisplayApps.Get(1).Name=="Clock" && DisplayApps.Get(6).Name=="Weather","Weather and Clock have distinct stable app identities");
        (settings with{HomeOrder=[6,1,0,4,2],OfflineApp=6}).Validate();Check(true,"Weather can be pinned and selected for offline use");
        Reject(settings with{HomeOrder=[0,1,2,3,4,6]},"six shortcuts do not overflow the five-slot Home layout");
        Check((settings with{OfflineApp=6}).SetEnabled(6,false).OfflineApp==1,"disabling the offline app falls back to Clock");
        Check((settings with{OfflineApp=1,EnabledApps=34,HomeOrder=[]}).SetEnabled(1,false).OfflineApp==5,"Home is the fallback when Clock is disabled");
        foreach(var bad in new[]{settings with{DimBehavior=3},settings with{DimAfterSeconds=0},settings with{DimBrightness=0},settings with{DimBrightness=101},settings with{NightStart=24},settings with{NightEnd=-1},settings with{NightStart=10,NightEnd=10},settings with{OfflineApp=8}})Reject(bad,"invalid screen policy rejected");
        (settings with{NightStart=22,NightEnd=7,DimBehavior=2,DimAfterSeconds=15,DimBrightness=5}).Validate();Check(true,"overnight ranges and always-idle dimming are supported");
        string legacy="{\"schema\":2,\"enabled_apps\":47,\"home_order\":[2,0,1],\"use_24h\":false,\"weather_enabled\":false,\"city\":\"Warsaw\",\"latitude\":52.2297,\"longitude\":21.0122,\"timezone\":\"CET-1CEST,M3.5.0,M10.5.0/3\"}";
        var migrated=DeviceSettingsClient.Parse(legacy);
        Check(migrated.ProtocolSchema==2 && migrated.Settings.Schema==DeviceSettings.CurrentSchema && migrated.Settings.EnabledApps==(1007|1024) && migrated.Settings.HomeOrder.SequenceEqual(new[]{2,0,1}) && !migrated.Settings.Use24h && !migrated.Settings.WeatherEnabled,"migration preserves disabled apps, ordering, time format and forecast visibility");
        Check(migrated.Settings.DimBehavior==1 && migrated.Settings.DimBrightness==40 && migrated.Settings.NightEnabled && migrated.Settings.NightStart==1 && migrated.Settings.NightEnd==10 && migrated.Settings.OfflineApp==1,"legacy preferences inherit the previous screen behavior");
        var custom=settings with{DimBehavior=2,DimAfterSeconds=120,DimBrightness=20,NightEnabled=false,NightStart=23,NightEnd=8,OfflineApp=6};
        Check(DeviceSettingsClient.Equivalent(custom,DeviceSettingsClient.Parse(JsonSerializer.Serialize(custom,DeviceSettingsClient.Json)).Settings),"every new screen preference survives wireless JSON roundtrip");
        Check(!DeviceSettingsClient.Equivalent(settings,settings with{NightEnabled=false}) && !DeviceSettingsClient.Equivalent(settings,settings with{DimBrightness=20}) && !DeviceSettingsClient.Equivalent(settings,settings with{OfflineApp=6}),"sync confirmation detects screen preference differences");
        Check(!new DeviceSettings().WeatherFahrenheit,"weather defaults to Celsius");
        var fahrenheitSettings=custom with{Language="pl",WeatherFahrenheit=true};
        string fahrenheitJson=JsonSerializer.Serialize(fahrenheitSettings,DeviceSettingsClient.Json);
        Check(fahrenheitJson.Contains("\"weather_fahrenheit\":true") && DeviceSettingsClient.Equivalent(fahrenheitSettings,DeviceSettingsClient.Parse(fahrenheitJson).Settings),"Fahrenheit round-trips with independent language and screen preferences");
        Check(!DeviceSettingsClient.Equivalent(custom,custom with{WeatherFahrenheit=true}),"unit mismatch cannot be falsely marked synced");
        var oldLanguage=fahrenheitSettings with{Schema=4};var upgraded=DeviceSettingsClient.Parse(JsonSerializer.Serialize(oldLanguage,DeviceSettingsClient.Json));
        Check(upgraded.ProtocolSchema==4 && DeviceSettingsClient.Equivalent(upgraded.Settings,fahrenheitSettings with{WeatherFahrenheit=false}),"schema 4 preserves language and preferences while defaulting weather to Celsius");
        bool incompleteUnits=false;try{DeviceSettingsClient.Parse(fahrenheitJson.Replace("\"weather_fahrenheit\":true,",""));}catch(IOException){incompleteUnits=true;}Check(incompleteUnits,"new settings require a temperature unit field");
        bool invalidUnits=false;try{DeviceSettingsClient.Parse(fahrenheitJson.Replace("\"weather_fahrenheit\":true","\"weather_fahrenheit\":\"fahrenheit\""));}catch(JsonException){invalidUnits=true;}Check(invalidUnits,"untyped temperature preferences are rejected");
        // Looks: the display and companion share themes/themes.json; older displays keep the crab.
        Check(Themes.Ids.SequenceEqual(new[]{"crab","studio","crabv2","crastudio"})&&Themes.AccentIds.Contains("champagne"),"the companion reads the same theme catalog as the firmware");
        // The cased looks wear this app's case: its orange, the crab only in Crab v2, and no accent to choose.
        var crabTwo=Themes.Get("crabv2","sage");var crastudio=Themes.Get("crastudio","sage");
        Check(crabTwo.Cased&&crastudio.Cased&&!Themes.Get("crab",null).Cased&&!Themes.Get("studio",null).Cased,"only Crab v2 and Crastudio sit in the orange case");
        Check(crabTwo.Mascot&&!crastudio.Mascot&&!crabTwo.HasAccents&&!crastudio.HasAccents&&crabTwo.Frame==ClawTheme.Case&&crastudio.Accent==ClawTheme.Case,"Crab v2 keeps the crab, Crastudio drops it, and both wear the app's orange");
        var cased=custom with{Theme="crastudio",Accent="champagne"};string casedJson=JsonSerializer.Serialize(cased,DeviceSettingsClient.Json);
        Check(casedJson.Contains("\"schema\":11")&&casedJson.Contains("\"theme\":\"crastudio\"")&&DeviceSettingsClient.Equivalent(cased,DeviceSettingsClient.Parse(casedJson).Settings),"a cased look round-trips with schema 11 settings");
        // Schema 11: the Printer app. Displays on schema 10 gain it switched on and ask for a firmware update; it can sit on Home but is never the offline app.
        var beforePrinter=DeviceSettingsClient.Parse(JsonSerializer.Serialize(custom with{EnabledApps=1023,Schema=10},DeviceSettingsClient.Json));
        Check(beforePrinter.ProtocolSchema==10&&beforePrinter.Settings.Schema==11&&beforePrinter.Settings.Enabled(10)&&beforePrinter.Settings.Theme==custom.Theme,"schema 10 displays gain the Printer app and keep everything else");
        var printerHome=new DeviceSettings() with{HomeOrder=[10,0]};printerHome.Validate();
        Check(!printerHome.SetEnabled(10,false).Enabled(10)&&!printerHome.SetEnabled(10,false).HomeOrder.Contains(10)&&DisplayApps.Get(10).Name.Length>0,"the Printer app can sit on Home and turning it off removes it there");
        bool printerOffline=false;try{(new DeviceSettings() with{OfflineApp=10}).Validate();}catch(ArgumentException){printerOffline=true;}Check(printerOffline,"the Printer app needs the PC, so it is never the offline app");
        bool twelfth=false;try{(new DeviceSettings() with{EnabledApps=4095}).Validate();}catch(ArgumentException){twelfth=true;}Check(twelfth,"no app beyond the Printer app");
        var beforeCases=DeviceSettingsClient.Parse(JsonSerializer.Serialize(custom with{Theme="studio",Accent="sage",Schema=9},DeviceSettingsClient.Json));
        Check(beforeCases.ProtocolSchema==9&&beforeCases.Settings.Schema==DeviceSettings.CurrentSchema&&beforeCases.Settings.Theme=="studio","schema 9 displays keep their look and ask for a firmware update");
        L.SetVariant("crastudio");bool calm=L.Variant=="studio";L.SetVariant("crabv2");bool crabby=L.Variant is null;L.SetVariant("crab");
        Check(calm&&crabby,"looks without the crab use the wording that never mentions it");
        Check(new DeviceSettings().Theme=="crab"&&!Themes.Get("crab","sage").HasAccents&&Themes.Get("crab","sage").Accent==Themes.Get("crab",null).Accent,"the crab keeps its own accent");
        Check(Themes.Get("studio","ice").Accent==Themes.AccentColor("ice")&&!Themes.Get("studio","ice").Mascot,"Studio takes the chosen accent and has no mascot");
        var studio=custom with{Theme="studio",Accent="sage"};string studioJson=JsonSerializer.Serialize(studio,DeviceSettingsClient.Json);
        Check(studioJson.Contains("\"theme\":\"studio\"")&&studioJson.Contains("\"accent\":\"sage\"")&&DeviceSettingsClient.Equivalent(studio,DeviceSettingsClient.Parse(studioJson).Settings),"the look round-trips with the wireless settings");
        Check(!DeviceSettingsClient.Equivalent(custom,studio)&&!DeviceSettingsClient.Equivalent(studio,studio with{Accent="ice"}),"a different look or accent cannot be falsely marked synced");
        var beforeLooks=DeviceSettingsClient.Parse(JsonSerializer.Serialize(studio with{Schema=6},DeviceSettingsClient.Json).Replace("\"theme\":\"studio\",","").Replace("\"accent\":\"sage\",",""));
        Check(beforeLooks.ProtocolSchema==6&&beforeLooks.Settings.Theme=="crab"&&beforeLooks.Settings.Accent=="champagne","schema 6 displays are read as the crab");
        bool missingLook=false;try{DeviceSettingsClient.Parse(studioJson.Replace("\"theme\":\"studio\",",""));}catch(IOException){missingLook=true;}Check(missingLook,"schema 7 settings require a theme");
        bool unknownLook=false;try{(custom with{Theme="neon"}).Validate();}catch(ArgumentException){unknownLook=true;}Check(unknownLook,"unknown looks are rejected before sending");
        bool setupLook=false;try{new SetupOptions("COM3","Home","password123","Warsaw",52.2,21.0,"UTC0",false,false,false,Theme:"studio",Accent:"gold").Validate();}catch(ArgumentException){setupLook=true;}Check(setupLook,"setup rejects an unknown accent");
        Exception? failure=null;
        var thread=new Thread(()=>{
            try {
                using var form=new Form(){Size=new(440,460),ShowInTaskbar=false};
                using var list=new HomeOrderList(settings){Size=new(370,310),Location=new(10,10)};
                using var toggle=new ClawSwitch(){Text="Test switch",Size=new(370,42),Location=new(10,335)};
                DeviceSettings? changed=null;list.Changed+=value=>changed=value;form.Controls.AddRange([list,toggle]);form.Show();Application.DoEvents();
                static IntPtr Point(int x,int y)=>new((y<<16)|x);
                SendMessage(list.Handle,0x201,new IntPtr(1),Point(15,25));
                SendMessage(list.Handle,0x200,new IntPtr(1),Point(15,145));
                SendMessage(list.Handle,0x202,IntPtr.Zero,Point(15,145));Application.DoEvents();
                Check(changed?.HomeOrder.SequenceEqual(new[]{4,7,0,2,1})==true,"native mouse drag changes Home order");
                SendMessage(list.Handle,0x201,new IntPtr(1),Point(350,25));SendMessage(list.Handle,0x202,IntPtr.Zero,Point(350,25));Application.DoEvents();
                Check(changed?.HomeOrder.SequenceEqual(new[]{7,0,2,1})==true,"remove action only unpins the selected shortcut");
                // CheckBox mouse-up also consults the real desktop pointer. Use native
                // Space-key dispatch so this check neither moves nor depends on the user's cursor.
                toggle.Focus();
                SendMessage(toggle.Handle,0x100,new IntPtr(0x20),IntPtr.Zero);
                SendMessage(toggle.Handle,0x101,new IntPtr(0x20),new IntPtr(unchecked((int)0xC0000001)));Application.DoEvents();
                Check(toggle.Checked,"custom switch retains native Space-key checkbox behavior");
                form.Close();
                using var previewConnection=new Connection(new Configuration());
                using var screen=new SettingsForm(new Configuration{DisplaySettings=settings},previewConnection,true);
                screen.Show();Application.DoEvents();
                static IEnumerable<Control> Descendants(Control parent) {foreach(Control c in parent.Controls){yield return c;foreach(var child in Descendants(c))yield return child;}}
                Descendants(screen).OfType<ClawButton>().Single(b=>b.Text=="Screen").PerformClick();Application.DoEvents();
                var fields=Descendants(screen).OfType<ChoiceBox>().ToArray();Check(fields.Length==1,"Screen uses a list only for choosing the offline app");
                var brightness=Descendants(screen).OfType<ClawSlider>().Single();
                var times=Descendants(screen).OfType<ClawTimeInput>().ToArray();Check(times.Length==3,"Delay and night hours are editable numeric time controls");
                Descendants(screen).OfType<ClawButton>().Single(b=>b.Text=="Never").PerformClick();Application.DoEvents();
                Check(!times.Single(f=>f.Format==ClawNumberFormat.Duration).Enabled&&!brightness.Enabled,"never-dim mode disables only dimming controls");
                Descendants(screen).OfType<ClawButton>().Single(b=>b.Text=="Whenever idle").PerformClick();
                Check(brightness.Enabled,"always-idle mode restores the brightness slider");
                var night=Descendants(screen).OfType<ClawSwitch>().Single(f=>f.Text.Contains("night hours"));night.Checked=false;
                Check(times.Where(f=>f.Format==ClawNumberFormat.Hour).All(f=>!f.Enabled)&&!night.IsDisposed,"night toggle safely disables both editable hour controls");
                night.Checked=true;Check(times.Where(f=>f.Format==ClawNumberFormat.Hour).All(f=>f.Enabled),"night hours can be re-enabled without replacing the page");
                Check(fields.Single(f=>f.Text=="Clock").Items.Cast<object>().Any(x=>x.ToString()=="Weather"),"Weather is selectable as the offline app");
                Descendants(screen).OfType<ClawButton>().Single(b=>b.Text==L.Text("clock_weather")).PerformClick();Application.DoEvents();
                var fahrenheit=Descendants(screen).OfType<ClawButton>().Single(b=>b.Text==L.Text("fahrenheit"));
                var celsius=Descendants(screen).OfType<ClawButton>().Single(b=>b.Text==L.Text("celsius"));
                Check(celsius.Selected&&!fahrenheit.Selected,"temperature buttons start on Celsius");
                fahrenheit.PerformClick();Application.DoEvents();
                var draft=(DeviceSettings)typeof(SettingsForm).GetField("draft",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(screen)!;
                Check(draft.WeatherFahrenheit&&fahrenheit.Selected&&!celsius.Selected&&draft.City==settings.City,"Fahrenheit button changes only the temperature choice");
                celsius.PerformClick();Check(celsius.Selected&&!fahrenheit.Selected,"Celsius can be selected again");
                screen.Close();
            }catch(Exception e){failure=e;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
        return passed;
    }
}

internal sealed class ScriptedHandler(string[] replies) : HttpMessageHandler
{
    private int next;
    public List<string> Paths { get; } = [];
    public List<string> Bodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        if(request.Content is not null)Bodies.Add(await request.Content.ReadAsStringAsync(token));
        string reply=replies[Math.Min(next++,replies.Length-1)];
        return reply=="404"?new HttpResponseMessage(System.Net.HttpStatusCode.NotFound):
            new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"state\":\""+reply+"\"}")};
    }
}
