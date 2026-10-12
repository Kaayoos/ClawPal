using System.Diagnostics;
namespace ClawPal.Companion;

internal static class CompanionUpgrade
{
    internal static bool IsNewer(string? installed, Version candidate) =>
        Version.TryParse(installed, out var previous) && candidate > previous;

    // Only an explicitly launched newer installer may replace this user's installed app.
    internal static void ApplyIfNewer(string[] args)
    {
        if(args.Length!=0 || Environment.ProcessPath is not { } source ||
           !File.Exists(Startup.InstalledPath) || StartupContract.SamePath(source,Startup.InstalledPath))return;
        if(!IsNewer(FileVersionInfo.GetVersionInfo(Startup.InstalledPath).FileVersion,
            typeof(CompanionUpgrade).Assembly.GetName().Version!))return;
        try { using var running=Mutex.OpenExisting(@"Local\ClawPal.Companion"); throw new IOException(L.Text("close_to_upgrade")); }
        catch(WaitHandleCannotBeOpenedException) { }
        var staging=Startup.InstalledPath+".new";
        try {
            File.Copy(source,staging,true);
            File.Replace(staging,Startup.InstalledPath,Startup.InstalledPath+".previous",true);
        }
        catch(IOException error) { throw new IOException(L.Text("close_to_upgrade"),error); }
        finally { if(File.Exists(staging))File.Delete(staging); }
    }
}
