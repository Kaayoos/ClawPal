using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClawPal.Companion;

// A local bridge, not an OAuth client. Credentials, transcripts, prompts and
// tool arguments are never persisted or sent to the display.
internal static class ClaudeBridge
{
    internal static string ClaudeDirectory=>Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is {Length:>0} custom
        ? Path.GetFullPath(custom):Path.Combine(WindowsFolders.Profile,".claude");
    internal static string DirectoryPath=>Path.Combine(Configuration.DirectoryPath,"claude");
    private static string PreviousStatus=>Path.Combine(DirectoryPath,"previous-statusline.json");
    private static readonly string[] Events=["SessionStart","SessionEnd","UserPromptSubmit","PreToolUse","PostToolUse","PermissionRequest","Notification","Stop","StopFailure"];
    internal static string Status=>Installed?L.Text("claude_link_ready"):L.Text("claude_link_missing");
    internal static bool Installed {
        get {try {var settings=JsonNode.Parse(File.ReadAllText(Path.Combine(ClaudeDirectory,"settings.json")));return settings?["statusLine"]?["command"]?.GetValue<string>()?.Contains("--claude-statusline",StringComparison.Ordinal)==true;}catch{return false;}}
    }
    internal static void Install()
    {
        if(!File.Exists(Startup.InstalledPath))throw new IOException(L.Text("claude_install_first"));
        InstallAt(ClaudeDirectory,DirectoryPath,Startup.InstalledPath);
    }
    internal static void InstallAt(string claudeDirectory,string bridgeDirectory,string executable)
    {
        Directory.CreateDirectory(claudeDirectory);Directory.CreateDirectory(bridgeDirectory);
        string path=Path.Combine(claudeDirectory,"settings.json");
        string original=File.Exists(path)?File.ReadAllText(path):"{}";
        if(original.Length>1_048_576)throw new IOException(L.Text("claude_settings_invalid"));
        var settings=JsonNode.Parse(original) as JsonObject??throw new IOException(L.Text("claude_settings_invalid"));
        string? bash=FindBash();
        string command=bash is null ? "& '"+executable.Replace("'","''")+"'" : "'"+executable.Replace('\\','/').Replace("'","'\"'\"'")+"'";
        var status=settings["statusLine"];
        if(status is not null && status is not JsonObject)throw new IOException(L.Text("claude_settings_invalid"));
        bool ours=status?["command"]?.GetValue<string>()?.Contains("--claude-statusline",StringComparison.Ordinal)==true;
        if(!ours)AtomicWrite(Path.Combine(bridgeDirectory,"previous-statusline.json"),status?.ToJsonString()??"null");
        var replacement=status?.DeepClone() as JsonObject??new JsonObject();
        replacement["type"]="command";replacement["command"]=command+" --claude-statusline";
        int interval=30;
        if(replacement["refreshInterval"] is JsonValue refresh&&refresh.TryGetValue<int>(out int requested)&&requested>=1)interval=Math.Min(interval,requested);
        replacement["refreshInterval"]=interval;settings["statusLine"]=replacement;
        if(settings["hooks"] is not null && settings["hooks"] is not JsonObject)throw new IOException(L.Text("claude_settings_invalid"));
        var hooks=settings["hooks"] as JsonObject??new JsonObject();settings["hooks"]=null;settings["hooks"]=hooks;
        foreach(string name in Events) {
            if(hooks[name] is not null && hooks[name] is not JsonArray)throw new IOException(L.Text("claude_settings_invalid"));
            var entries=hooks[name] as JsonArray??new JsonArray();hooks[name]=null;hooks[name]=entries;
            // Remove only our own entries, leaving other integrations untouched.
            for(int i=entries.Count-1;i>=0;i--)if(entries[i]?["hooks"] is JsonArray actions) {
                for(int j=actions.Count-1;j>=0;j--)if(actions[j]?["command"]?.GetValue<string>()?.Contains("--claude-event",StringComparison.Ordinal)==true)actions.RemoveAt(j);
                if(actions.Count==0)entries.RemoveAt(i);
            }
            entries.Add(new JsonObject { ["hooks"]=new JsonArray(new JsonObject { ["type"]="command",["command"]=command+" --claude-event",["shell"]=bash is null?"powershell":"bash",["timeout"]=3 }) });
        }
        string updated=settings.ToJsonString(new JsonSerializerOptions{WriteIndented=true});
        if(updated==original)return;
        if(File.Exists(path)&&!File.Exists(Path.Combine(bridgeDirectory,"settings-before-clawpal.json")))File.Copy(path,Path.Combine(bridgeDirectory,"settings-before-clawpal.json"));
        // Refuse to overwrite a concurrent settings edit.
        if(File.Exists(path)&&File.ReadAllText(path)!=original)throw new IOException(L.Text("claude_settings_changed"));
        AtomicWrite(path,updated);
    }
    internal static int Receive(bool statusLine)
    {
        string input="";
        try {
            using var stream=Console.OpenStandardInput();using var reader=new StreamReader(stream,Encoding.UTF8);
            var buffer=new char[4096];var text=new StringBuilder();int count;
            while((count=reader.Read(buffer,0,buffer.Length))>0){if(text.Length+count>1_048_576)return 0;text.Append(buffer,0,count);}
            input=text.ToString();
            using var json=JsonDocument.Parse(input);
            if(Configuration.Load().DisplaySettings?.Enabled(7)!=false)Store(json.RootElement,statusLine);
        } catch { /* A monitor failure must never interrupt a Claude session. */ }
        if(statusLine)ForwardStatusLine(input);
        return 0;
    }
    private static void Store(JsonElement root,bool statusLine)
    {
        string? id=CodexContract.String(root,"session_id");
        if(id is null || id.Length>128 || !id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))return;
        Directory.CreateDirectory(DirectoryPath);
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        using var mutex=new Mutex(false,@"Local\ClawPal.Claude."+key);bool acquired=false;
        try {
            try{acquired=mutex.WaitOne(500);}catch(AbandonedMutexException){acquired=true;}if(!acquired)return;
            string path=Path.Combine(DirectoryPath,key+".session.json");ClaudeSession? previous=null;
            if(File.Exists(path)&&new FileInfo(path).Length<16384)try{previous=JsonSerializer.Deserialize<ClaudeSession>(File.ReadAllText(path));}catch(JsonException){}
            var owner=OwnerProcess();
            var value=ClaudeContract.Observe(root,statusLine,previous,DateTimeOffset.UtcNow,owner.Id,owner.Started);
            if(value is not null)AtomicWrite(path,JsonSerializer.Serialize(value));
        }finally{if(acquired)mutex.ReleaseMutex();}
    }
    private static void ForwardStatusLine(string input)
    {
        try {
            var saved=File.Exists(PreviousStatus)?JsonNode.Parse(File.ReadAllText(PreviousStatus)):null;
            string? command=saved?["command"]?.GetValue<string>();
            using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
            if(string.IsNullOrWhiteSpace(command)) {
                using var document=JsonDocument.Parse(input);
                if(CodexContract.Object(document.RootElement,"model",out var model))output.Write(MusicContract.LimitText(CodexContract.String(model,"display_name")??"",63));
                return;
            }
            string? bash=FindBash();
            var start=new ProcessStartInfo(bash??"powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            if(bash is null){start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");}else start.ArgumentList.Add("-c");start.ArgumentList.Add(command);
            using var process=Process.Start(start);if(process is null)return;
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input);process.StandardInput.Close();
            if(!process.WaitForExit(1800)){process.Kill(true);return;}
            output.Write(stdout.GetAwaiter().GetResult()); // Original command sees the original stdin.
        }catch { }
    }
    private static string? FindBash()
    {
        string? configured=Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        if(configured is not null&&File.Exists(configured))return configured;
        foreach(string root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}){
            string candidate=Path.Combine(root,root.EndsWith("Local")?"Programs/Git/bin/bash.exe":"Git/bin/bash.exe");if(File.Exists(candidate))return candidate;
        }
        foreach(string directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){
            try{string path=Path.Combine(directory,"bash.exe");if(File.Exists(path)&&!path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows),StringComparison.OrdinalIgnoreCase))return path;}catch(ArgumentException){}
        }
        return null;
    }
    internal static bool IsAlive(ClaudeSession session)
    {
        if(session.ProcessId<=0)return DateTimeOffset.UtcNow-session.Seen<TimeSpan.FromSeconds(90);
        try{using var p=Process.GetProcessById(session.ProcessId);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==session.ProcessStarted;}catch{return false;}
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo {public nint Reserved1,Peb,Reserved2,Reserved3,UniquePid,ParentPid;}
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process,int kind,ref BasicInfo info,int length,out int returned);
    private static (int Id,long Started) OwnerProcess()
    {
        try {
            int id=Environment.ProcessId;
            for(int depth=0;depth<8;depth++){
                using var p=Process.GetProcessById(id);
                if(depth>0 && (p.ProcessName.Equals("claude",StringComparison.OrdinalIgnoreCase)||p.ProcessName.Equals("node",StringComparison.OrdinalIgnoreCase)))return(p.Id,p.StartTime.ToUniversalTime().Ticks);
                var info=new BasicInfo();if(NtQueryInformationProcess(p.Handle,0,ref info,Marshal.SizeOf<BasicInfo>(),out _)!=0)break;
                id=(int)info.ParentPid;if(id<=0)break;
            }
        }catch { }
        return(0,0);
    }
    internal static void AtomicWrite(string path,string value)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,value,new UTF8Encoding(false));File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}

internal sealed class ClaudeUsage
{
    internal Func<bool> Enabled {get;init;}=()=>true;
    private CodexStats snapshot=CodexStats.Empty(false,"closed");
    internal CodexStats Snapshot=>Volatile.Read(ref snapshot);
    private ClaudeDesktopReading? desktopReading;
    private DateTimeOffset nextDesktopRead;
    private string? desktopSession;
    internal CodexStats Refresh()
    {
        var now=DateTimeOffset.UtcNow;
        var rows=new List<ClaudeSession>();
        if(Directory.Exists(ClaudeBridge.DirectoryPath))foreach(var file in new DirectoryInfo(ClaudeBridge.DirectoryPath).EnumerateFiles("*.session.json").OrderByDescending(f=>f.LastWriteTimeUtc).Take(64)) {
            if(file.Length>16384)continue;
            if(file.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-2)){file.Delete();continue;}
            try{var s=JsonSerializer.Deserialize<ClaudeSession>(File.ReadAllText(file.FullName));if(s is not null)rows.Add(s);}catch(JsonException){}catch(IOException){}
        }
        string? latest=ClaudeDesktop.LatestLive(rows,now,ClaudeBridge.IsAlive)?.Id;
        if(latest!=desktopSession||now>=nextDesktopRead){
            desktopReading=ClaudeDesktop.Read(rows,now,ClaudeBridge.IsAlive);
            desktopSession=latest;nextDesktopRead=now.AddSeconds(10);
        }
        return ClaudeDesktop.Merge(ClaudeContract.Snapshot(rows,now,ClaudeBridge.IsAlive),desktopReading,now);
    }
    internal async Task RunAsync(CancellationToken cancellation)
    {
        bool installAttempted=false;
        while(!cancellation.IsCancellationRequested){
            try {
                if(Enabled()){
                    if(!installAttempted&&Directory.Exists(ClaudeBridge.ClaudeDirectory)){
                        installAttempted=true;if(!ClaudeBridge.Installed)ClaudeBridge.Install();
                    }
                    Volatile.Write(ref snapshot,Refresh());
                } else {desktopReading=null;desktopSession=null;nextDesktopRead=default;Volatile.Write(ref snapshot,CodexStats.Empty(false,"closed"));}
            } catch(Exception e) when(e is not OperationCanceledException){Log.Write("Claude monitor unavailable: "+e.GetType().Name);Volatile.Write(ref snapshot,CodexStats.Empty(false,"error"));}
            try{await Task.Delay(2000,cancellation);}catch(OperationCanceledException){break;}
        }
    }
}
