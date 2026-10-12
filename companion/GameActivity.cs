using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawPal.Companion;

public sealed class GameActivity : IDisposable
{
    public Func<bool> Enabled { get; set; } = () => true;
    private readonly CancellationTokenSource stop = new();
    private readonly string helper = Path.Combine(Installer.PayloadPath, "tools", "PresentMon", "PresentMon.exe");
    private string sessionName = "ClawPal-FPS-" + Environment.UserName;
    private GameStats snapshot = GameStats.Idle;
    private double gpuUsage = double.NaN;
    private Process? capture;
    private Task? reader, errors;
    private PresentMonFrames frames = new();
    private long captureStarted;
    public GameStats Snapshot => Volatile.Read(ref snapshot);
    public void SetGpuUsage(double? usage) => Interlocked.Exchange(ref gpuUsage, usage ?? double.NaN);

    public async Task RunAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        token = linked.Token;
        int activePid = 0, candidatePid = 0, consecutive = 0;
        long retryAt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var candidate = Enabled() ? Foreground() : null;
                bool eligible = candidate is { } app && GameContract.ShouldCapture(app.Process, app.KnownGame, app.FullScreen, Volatile.Read(ref gpuUsage), activePid == app.Pid);
                if (!eligible)
                {
                    candidatePid = consecutive = activePid = 0;
                    Volatile.Write(ref snapshot, GameStats.Idle);
                    await StopCapture();
                }
                else if (candidate is { } game)
                {
                    consecutive = candidatePid == game.Pid ? consecutive + 1 : 1;
                    candidatePid = game.Pid;
                    if (activePid != game.Pid)
                    {
                        await StopCapture(); activePid = 0; retryAt = 0;
                        Volatile.Write(ref snapshot, GameStats.Idle);
                    }
                    if (consecutive >= 3)
                    {
                        activePid = game.Pid;
                        if ((capture is null || capture.HasExited) && Environment.TickCount64 >= retryAt)
                        {
                            await StopCapture();
                            retryAt = Environment.TickCount64 + 30000;
                            StartCapture(game.Pid);
                        }
                        double? fps = capture is { HasExited: false } ? frames.Fps(Environment.TickCount64) : null;
                        string status = fps.HasValue ? "measured" : capture is { HasExited: false } && Environment.TickCount64 - captureStarted < 10000 ? "measuring" : "unavailable";
                        Volatile.Write(ref snapshot, new(true, game.Title, fps, status));
                    }
                }
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Volatile.Write(ref snapshot, GameStats.Idle); await StopCapture(); }
    }

    private void StartCapture(int pid)
    {
        if (!File.Exists(helper)) return;
        frames = new();
        try
        {
            var start = HiddenProcess();
            foreach (var arg in new[] { "--process_id", pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--output_stdout", "--v1_metrics", "--no_console_stats", "--no_track_input",
                "--no_track_gpu", "--no_track_display", "--session_name", sessionName,
                "--stop_existing_session", "--terminate_on_proc_exit", "--timed", "120", "--terminate_after_timed" }) start.ArgumentList.Add(arg);
            capture = Process.Start(start);
            if (capture is null) return;
            captureStarted = Environment.TickCount64;
            var process = capture; var accumulator = frames;
            reader = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line) accumulator.Add(line, pid, Environment.TickCount64);
            });
            // Drain warnings without retaining frame logs or process names on disk.
            errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) { } });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { Log.Write("Game FPS capture unavailable: " + e.GetType().Name); capture?.Dispose(); capture = null; }
    }

    private ProcessStartInfo HiddenProcess() => new(helper)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(helper)!
    };

    private async Task StopCapture()
    {
        var process = capture; capture = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                var end = HiddenProcess();
                end.ArgumentList.Add("--session_name"); end.ArgumentList.Add(sessionName);
                end.ArgumentList.Add("--terminate_existing_session");
                using var terminator = Process.Start(end);
                if (terminator is not null)
                {
                    var stdout = terminator.StandardOutput.ReadToEndAsync(); var stderr = terminator.StandardError.ReadToEndAsync();
                    try { await terminator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException) { terminator.Kill(); }
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
                }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            if (reader is not null && errors is not null) await Task.WhenAll(reader, errors).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
        finally { process.Dispose(); reader = errors = null; }
    }

    public async Task<object> DiagnoseAsync(CancellationToken token)
    {
        sessionName += "-Diagnostic-" + Environment.ProcessId;
        var app = Foreground();
        if (app is null) return new { status = "no_foreground_window" };
        try
        {
            StartCapture(app.Pid);
            await Task.Delay(6000, token);
            return new { status = capture is { HasExited: false } ? "capture_running" : "capture_unavailable",
                fps = frames.Fps(Environment.TickCount64), process_id = app.Pid,
                game_candidate = GameContract.ShouldCapture(app.Process, app.KnownGame, app.FullScreen, 70, false) };
        }
        finally { await StopCapture(); }
    }

    private sealed record Candidate(int Pid, string Process, string Title, bool KnownGame, bool FullScreen);
    private static Candidate? Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            string name = process.ProcessName;
            var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
            string path = "";
            try { path = process.MainModule?.FileName ?? ""; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            var monitor = MonitorFromWindow(window, 2); var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            bool fullScreen = GetWindowRect(window, out var rect) && GetMonitorInfo(monitor, ref info) &&
                rect.Left <= info.Monitor.Left + 2 && rect.Top <= info.Monitor.Top + 2 &&
                rect.Right >= info.Monitor.Right - 2 && rect.Bottom >= info.Monitor.Bottom - 2;
            return new((int)pid, name, MusicContract.LimitText(title.Length > 0 ? title.ToString() : name, 95), GameContract.IsKnownGamePath(path), fullScreen);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return null; }
    }
    public void Dispose() { stop.Cancel(); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
