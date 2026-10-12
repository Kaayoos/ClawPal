using ClawPal.Companion;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

internal static class LocalizationChecks
{
    internal static int Run()
    {
        int passed=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);passed++;Console.WriteLine("PASS: "+name);}
        Check(new[]{"en","pl"}.All(code=>L.Languages.Any(l=>l.Code==code)),"language packs are discovered from embedded JSON");
        var en=new DeviceSettings{Language="en",City="Łódź",HomeOrder=[6,0,3],DimBrightness=25,DimAfterSeconds=300,NightStart=23,NightEnd=8,Use24h=false};
        var polish=en with{Language="pl"};polish.Validate();
        Check(DeviceSettingsClient.Equivalent(polish,DeviceSettingsClient.Parse(JsonSerializer.Serialize(polish,DeviceSettingsClient.Json)).Settings),"display language survives the wireless settings round-trip");
        Check(!DeviceSettingsClient.Equivalent(en,polish),"language mismatch keeps settings pending instead of reporting success");
        var legacy=en with{Schema=3};var migrated=DeviceSettingsClient.Parse(JsonSerializer.Serialize(legacy,DeviceSettingsClient.Json)).Settings;
        Check(DeviceSettingsClient.Equivalent(migrated,en),"schema 3 upgrades to English while preserving all screen preferences");
        bool rejected=false;try{(en with{Language="not-installed"}).Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"unknown display languages are rejected before sync");
        var assembly=typeof(L).Assembly;
        Dictionary<string,Dictionary<string,string>> Pack(string code){using var stream=assembly.GetManifestResourceStream("ClawPal.Languages."+code+".json")!;using var doc=JsonDocument.Parse(stream);return new[]{"companion","display"}.ToDictionary(s=>s,s=>doc.RootElement.GetProperty(s).EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!));}
        var english=Pack("en");var translated=Pack("pl");
        foreach(string section in new[]{"companion","display"})Check(english[section].Keys.Order().SequenceEqual(translated[section].Keys.Order()),section+" translation covers every English key");
        foreach(var language in L.Languages){
            L.SetLanguage(language.Code);
            foreach(var entry in Pack(language.Code)["companion"]){
                if(entry.Value.Contains('{'))_ = System.Text.CompositeFormat.Parse(entry.Value);
                Check(L.Text(entry.Key)==entry.Value,"catalog lookup "+language.Code+"/"+entry.Key);
            }
            var progress=UsbToolOutput.ReadProgress("8388608 (50 %)",16777216,TimeSpan.FromMinutes(3));
            Check(progress is not null&&progress.Contains("03:00")&&progress.Contains("50%"),"localized backup progress preserves percent and elapsed time: "+language.Code);
        }
        string oldCaption=L.Text("connected_0","192.168.1.2");L.SetLanguage("en");
        Check(L.Retranslate(oldCaption)=="Connected · 192.168.1.2","formatted status updates when the PC language changes");
        L.SetLanguage("missing");Check(L.Language=="en","unknown saved PC language safely falls back to English");
        Exception? failure=null;
        var thread=new Thread(()=>{
            try{
                using var connection=new Connection(new Configuration());
                using var setup=new SetupForm(new Configuration(),connection,true);setup.Show();Application.DoEvents();
                IEnumerable<Control> Descendants(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
                var pickers=Descendants(setup).OfType<ChoiceBox>().Where(c=>c.Items.Any(i=>i is LanguageChoice)).ToArray();
                Check(pickers.Length==2,"first install offers separate PC and display languages");
                // Switching the shared language must not dispose the menu currently dispatching a click.
                L.SetLanguage("pl");Application.DoEvents();
                Check(!pickers[0].IsDisposed&&Descendants(setup).OfType<ClawButton>().Any(b=>b.Text=="Dalej"),"setup changes language live without disposing persistent inputs");
                setup.Hide();
                using var form=new SettingsForm(new Configuration{CompanionLanguage="pl",DisplaySettings=en},connection,true);form.Show();Application.DoEvents();
                Descendants(form).OfType<ClawButton>().Single(b=>b.Text=="Język").PerformClick();Application.DoEvents();
                var selectors=Descendants(form).OfType<ChoiceBox>().ToArray();
                Check(selectors.Length==2&&((LanguageChoice)selectors[0].SelectedItem!).Code=="pl"&&((LanguageChoice)selectors[1].SelectedItem!).Code=="en","settings keeps the PC and display languages independent");
                selectors[1].SelectedIndex=1;Application.DoEvents();
                var draft=(DeviceSettings)typeof(SettingsForm).GetField("draft",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
                Check(draft.Language=="pl"&&draft.HomeOrder.SequenceEqual(en.HomeOrder)&&draft.DimBrightness==25,"changing language preserves the draft screen preferences");
                L.SetLanguage("en");Application.DoEvents();
                Check(Descendants(form).OfType<ClawButton>().Any(b=>b.Text=="Language"),"language settings remain discoverable after a live switch");
                form.Close();setup.Close();
            }catch(Exception e){failure=e;}finally{L.SetLanguage("en");}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
        return passed;
    }
}
