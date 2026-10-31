using ClawPal.Companion;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ClaudeChecks
{
    internal static int Run(string directory)
    {
        int passed=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS: Claude "+label);}
        var now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        ClaudeSession? Read(string text,bool status=true,ClaudeSession? prior=null,DateTimeOffset? at=null){using var json=JsonDocument.Parse(text);return ClaudeContract.Observe(json.RootElement,status,prior,at??now,123,456);}
        var row=Read("""{"session_id":"fixture-1","session_name":"Release check","cwd":"C:/Projects/Example","model":{"display_name":"Sonnet"},"rate_limits":{"five_hour":{"used_percentage":27,"resets_at":1800001800},"seven_day":{"used_percentage":100,"resets_at":1800300000}},"prompt":"private prompt","transcript_path":"private transcript","access_token":"private secret"}""")!;
        Check(row.Primary.RemainingPercent==73&&row.Secondary.Available&&row.Secondary.RemainingPercent==0,"percentage means remaining; exhausted quota is available");
        Check(row.Primary.WindowMinutes==300&&row.Secondary.WindowMinutes==10080,"session and weekly windows are distinct");
        Check(row.Title=="Release check"&&row.Project=="Example","named session and project basename are retained");
        var encoded=JsonSerializer.Serialize(row);
        Check(!encoded.Contains("private")&&!encoded.Contains("Sonnet")&&!encoded.Contains("C:/"),"secrets, prompts, transcript paths and model are discarded");
        Check(Read("""{"session_id":"../escape"}""")==null,"path traversal session identifiers rejected");
        Check(Read("""{"session_id":"valid","rate_limits":{"five_hour":{"used_percentage":-1,"resets_at":1800001800}}}""")!.Primary.Available==false,"negative usage rejected");
        Check(Read("""{"session_id":"valid","rate_limits":{"five_hour":{"used_percentage":101,"resets_at":1800001800}}}""")!.Primary.Available==false,"over-range subscription usage rejected");
        Check(Read("""{"session_id":"valid","rate_limits":{"five_hour":{"used_percentage":"42","resets_at":1800001800}}}""")!.Primary.Available==false,"string usage rejected");
        Check(Read("""{"session_id":"valid","rate_limits":{"five_hour":{"used_percentage":42,"resets_at":1700000000}}}""")!.Primary.Available==false,"expired windows unavailable");
        Check(Read("""{"session_id":"valid","context_window":{"used_percentage":10}}""")!.Primary.Available==false,"context usage is never a subscription quota");
        var cleared=Read("""{"session_id":"fixture-1","rate_limits":null}""",prior:row)!;
        Check(!cleared.Primary.Available&&!cleared.Secondary.Available,"missing live limits clear an older account snapshot");
        var submit=Read("""{"session_id":"fixture-1","hook_event_name":"UserPromptSubmit","prompt":"secret"}""",false,row)!;
        Check(submit.State=="working"&&submit.Primary==row.Primary,"prompt lifecycle keeps usage without retaining prompt");
        var wait=Read("""{"session_id":"fixture-1","hook_event_name":"PermissionRequest","tool_input":{"command":"private"}}""",false,submit)!;
        Check(wait.State=="waiting","permission requests are waiting, not working");
        var stop=Read("""{"session_id":"fixture-1","hook_event_name":"Stop"}""",false,wait)!;
        Check(stop.State=="completed","Stop records completed work");
        var closed=Read("""{"session_id":"fixture-1","hook_event_name":"SessionEnd"}""",false,stop)!;
        var snapshot=ClaudeContract.Snapshot([closed],now,_=>true);
        Check(!snapshot.Running&&snapshot.Stale&&snapshot.Activity!.WorkingCount==0,"session end wins over a live process");
        snapshot=ClaudeContract.Snapshot([submit],now.AddSeconds(121),_=>true);
        Check(snapshot.Stale&&snapshot.Activity!.Items[0].State=="unknown","aged usage is cached and work expires");
        snapshot=ClaudeContract.Snapshot([submit],now,_=>false);
        Check(!snapshot.Running&&snapshot.Activity!.WorkingCount==0,"crashed processes cannot remain working");
        snapshot=ClaudeContract.Snapshot([row with{Id="other",Ended=true},cleared],now,_=>true);
        Check(!snapshot.Available,"live account without quota cannot borrow closed account quota");
        snapshot=ClaudeContract.Snapshot([row],now.AddDays(2),_=>true);
        Check(!snapshot.Running&&!snapshot.Available&&!snapshot.Activity!.Available,"old snapshots disappear");
        var many=Enumerable.Range(0,40).Select(i=>submit with{Id=i.ToString()}).ToArray();
        snapshot=ClaudeContract.Snapshot(many,now,_=>true);
        Check(snapshot.Activity!.TotalCount==32&&snapshot.Activity.Items.Length==3,"session enumeration and wire rows are bounded");
        var longRow=Read(JsonSerializer.Serialize(new {session_id="unicode",session_name=new string('ą',80),cwd="C:/"+new string('ł',60)}))!;
        Check(Encoding.UTF8.GetByteCount(longRow.Title)<=63&&Encoding.UTF8.GetByteCount(longRow.Project)<=31,"UTF-8 title and project fit device buffers");
        var settings=new DeviceSettings {Schema=5,EnabledApps=47,Language="pl",HomeOrder=[2,0,1],Use24h=false,WeatherFahrenheit=true,NightStart=23,NightEnd=7};
        var upgraded=settings.Upgrade();upgraded.Validate();
        Check(upgraded.Schema==DeviceSettings.CurrentSchema&&upgraded.Theme==Themes.Default&&upgraded.Enabled(7)&&upgraded.HomeOrder.SequenceEqual(settings.HomeOrder)&&upgraded.Language=="pl"&&upgraded.WeatherFahrenheit&&upgraded.NightStart==23,"schema migration adds Claude while retaining all preferences");
        (upgraded with{HomeOrder=[7],OfflineApp=7}).Validate();Check(DisplayApps.Get(7).Name=="Claude Code","Claude is independently selectable and pinnable");
        string root=Path.Combine(directory,"claude-bridge");string native=Path.Combine(root,"native"),bridge=Path.Combine(root,"bridge");Directory.CreateDirectory(native);
        string path=Path.Combine(native,"settings.json");
        var original="""{"permissions":{"allow":["Read"]},"statusLine":{"type":"command","command":"printf 'existing status'","padding":2},"hooks":{"Stop":[{"matcher":"","hooks":[{"type":"command","command":"echo existing"}]}]}}""";
        File.WriteAllText(path,original);
        ClaudeBridge.InstallAt(native,bridge,"C:/Program Files/Example's ClawPal/ClawPal.exe");
        var doc=JsonNode.Parse(File.ReadAllText(path))!;
        Check(doc["statusLine"]!["padding"]!.GetValue<int>()==2,"status-line visual preferences are retained");
        Check(doc["permissions"]!["allow"]![0]!.GetValue<string>()=="Read","unrelated settings remain intact");
        Check(doc["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()=="echo existing","other hook commands stay intact");
        Check(File.ReadAllText(Path.Combine(bridge,"settings-before-clawpal.json"))==original,"original settings backup is exact");
        var prior=JsonNode.Parse(File.ReadAllText(Path.Combine(bridge,"previous-statusline.json")))!;
        Check(prior["command"]!.GetValue<string>()=="printf 'existing status'"&&prior["padding"]!.GetValue<int>()==2,"original status line configuration preserved");
        string once=File.ReadAllText(path);ClaudeBridge.InstallAt(native,bridge,"C:/Program Files/Example's ClawPal/ClawPal.exe");
        Check(once==File.ReadAllText(path),"repair is idempotent and does not duplicate hooks");
        Check(File.ReadAllText(Path.Combine(bridge,"settings-before-clawpal.json"))==original,"repair retains first backup");
        ClaudeBridge.UninstallAt(native,bridge);
        doc=JsonNode.Parse(File.ReadAllText(path))!;
        Check(doc["statusLine"]!["command"]!.GetValue<string>()=="printf 'existing status'"&&doc["statusLine"]!["padding"]!.GetValue<int>()==2,"disconnect restores the original status line");
        Check(doc["hooks"]!.AsObject().Count==1&&doc["hooks"]!["Stop"]!.AsArray().Count==1&&doc["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()=="echo existing","disconnect removes only ClawPal hooks");
        Check(doc["permissions"]!["allow"]![0]!.GetValue<string>()=="Read"&&!File.Exists(Path.Combine(bridge,"previous-statusline.json")),"disconnect keeps unrelated settings and forgets the saved status line");
        File.WriteAllText(path,"{}");ClaudeBridge.InstallAt(native,bridge,"example.exe");ClaudeBridge.UninstallAt(native,bridge);
        Check(JsonNode.Parse(File.ReadAllText(path))!.AsObject().Count==0,"disconnect leaves no trace when nothing was configured before");
        File.WriteAllText(path,"{broken");bool rejected=false;try{ClaudeBridge.InstallAt(native,bridge,"example.exe");}catch(JsonException){rejected=true;}
        Check(rejected&&File.ReadAllText(path)=="{broken","invalid Claude settings are never overwritten");
        return passed + ClaudeDesktopChecks.Run(directory) + ClaudeLiveChecks.Run().GetAwaiter().GetResult();
    }
}
