using System.Diagnostics;
using Microsoft.Win32;

namespace ClawPal.Companion;

// Windows' installed-apps entry and the --uninstall flow. Everything is per user, so no
// administrator rights are needed. The display keeps whatever firmware it runs.
internal static class Uninstaller
{
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClawPal";
    // The tray app exits when this is set, unless it is writing to the display.
    internal const string ExitSignal = @"Local\ClawPal.Exit";
    private const string RunningMutex = @"Local\ClawPal.Companion";

    internal static IReadOnlyDictionary<string, object> Entry(string executable, string version, long bytes) => new Dictionary<string, object>
    {
        ["DisplayName"] = "ClawPal",
        ["DisplayVersion"] = version,
        ["Publisher"] = "MinimalPulse",
        ["DisplayIcon"] = executable + ",0",
        ["InstallLocation"] = Path.GetDirectoryName(executable)!,
        ["UninstallString"] = Quote(executable) + " --uninstall",
        ["QuietUninstallString"] = Quote(executable) + " --uninstall --quiet",
        ["URLInfoAbout"] = "https://github.com/Kaayoos/clawpal",
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
        ["EstimatedSize"] = (int)Math.Clamp(bytes / 1024, 0, int.MaxValue),
    };

    // Rewritten on every launch of the installed app, so the version follows upgrades.
    internal static void Register()
    {
        string executable = Startup.InstalledPath;
        if (!File.Exists(executable)) return;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(Path.GetDirectoryName(executable)!).EnumerateFiles("*", SearchOption.AllDirectories)) bytes += file.Length;
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        foreach (var (name, value) in Entry(executable, typeof(Uninstaller).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", bytes))
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        if (key.GetValue("InstallDate") is null) key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    internal static int Run(bool quiet)
    {
        string data = Configuration.DirectoryPath, backups = Installer.BackupsPath;
        if (!quiet && MessageBox.Show(L.Text("uninstall_confirm"), L.Text("uninstall_title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 1;
        // Backups are the only copy of the display's original firmware, so a quiet uninstall keeps them.
        bool keepBackups = Directory.Exists(backups) && Directory.EnumerateFileSystemEntries(backups).Any() &&
            (quiet || MessageBox.Show(L.Text("uninstall_keep_backups", backups), L.Text("uninstall_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
        Log.Write($"Uninstall started; keep backups={keepBackups}.");
        if (!StopRunningCompanion())
        {
            if (!quiet) MessageBox.Show(L.Text("uninstall_close_first"), L.Text("uninstall_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        string? claudeProblem = null;
        try { ClaudeBridge.Uninstall(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { claudeProblem = error.Message; }
        Startup.Set(false);
        try { if (File.Exists(Startup.StartMenuPath)) File.Delete(Startup.StartMenuPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
        // Nothing may log after this point: the log lives in the folder being removed.
        RemoveData(data, keepBackups ? backups : null);
        if (!quiet) MessageBox.Show(claudeProblem is null ? L.Text("uninstall_done") : L.Text("uninstall_claude_failed", claudeProblem),
            L.Text("uninstall_title"), MessageBoxButtons.OK, claudeProblem is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return 0;
    }

    // Asks the tray app to exit and waits for its single-instance mutex to disappear.
    private static bool StopRunningCompanion()
    {
        if (!Running()) return true;
        using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignal)) signal.Set();
        for (int i = 0; i < 100; ++i)
        {
            Thread.Sleep(100);
            if (!Running()) return true;
        }
        return false;
    }

    private static bool Running()
    {
        try { using var mutex = Mutex.OpenExisting(RunningMutex); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    private static void RemoveData(string data, string? keep)
    {
        if (!Directory.Exists(data)) return;
        string app = Path.GetDirectoryName(Startup.InstalledPath)!;
        bool runningFromApp = StartupContract.SamePath(Path.GetDirectoryName(Environment.ProcessPath), app);
        foreach (var entry in Directory.EnumerateFileSystemEntries(data).ToArray())
        {
            if ((keep is not null && StartupContract.SamePath(entry, keep)) || (runningFromApp && StartupContract.SamePath(entry, app))) continue;
            try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (runningFromApp) ScheduleRemoval(app, keep is null ? data : null);
        else if (keep is null)
            try { Directory.Delete(data, false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // A running executable cannot delete itself; cmd removes it shortly after this process exits.
    private static void ScheduleRemoval(string app, string? data)
    {
        string command = "ping 127.0.0.1 -n 4 >nul & rmdir /s /q " + Quote(app) + (data is null ? "" : " & rmdir " + Quote(data));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + command + "\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath(),
        };
        try { using var process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
