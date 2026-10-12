using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Windows.Management.Deployment;

namespace ClawPal.Companion;

// Detecting the desktop and querying its runtime are separate workers: a slow quota
// request cannot delay one-second telemetry or desktop launch/close detection.
public sealed class CodexUsage
{
    public Func<bool> Enabled { get; init; } = () => true;
    private readonly object gate = new();
    private CodexStats value = CodexStats.Empty(false, "unavailable");
    private DesktopRuntime desktop = new(false, null);
    private long capturedAt;
    private bool refreshFailed;
    private string? accountFingerprint;
    public string DiagnosticStage { get; private set; } = "detect";
    public string DiagnosticError { get; private set; } = "";

    public CodexStats Snapshot
    {
        get
        {
            lock (gate)
            {
                var age = capturedAt == 0 ? 0 : (long)Math.Clamp(Stopwatch.GetElapsedTime(capturedAt).TotalSeconds, 0, 31536000);
                var stale = value.Available && (!desktop.Running || refreshFailed || age > 120);
                return value with { Running = desktop.Running, AgeS = age, Stale = stale, Status = stale ? "cached" : value.Status };
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var detector = DetectAsync(lifetime.Token);
        var poller = PollAsync(lifetime.Token);
        try { await await Task.WhenAny(detector, poller); }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(detector, poller); } catch (OperationCanceledException) { }
        }
    }

    private async Task DetectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var found = Enabled() ? DesktopRuntime.FindRunning() : new DesktopRuntime(false,null);
            lock (gate)
            {
                desktop = found;
                if (!value.Available && !found.Running) value = CodexStats.Empty(false, "unavailable");
                else if (!value.Available && value.Status == "unavailable") value = CodexStats.Empty(true, "waiting");
            }
            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        CodexRpc? client = null;
        string? selectedPath = null;
        long nextPoll = 0;
        int failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DesktopRuntime current;
                lock (gate) current = desktop;
                if (!current.Running)
                {
                    if (client is not null) { await client.DisposeAsync(); client = null; }
                    selectedPath = null;
                    nextPoll = 0;
                    failures = 0;
                }
                else if (nextPoll == 0 || Stopwatch.GetTimestamp() >= nextPoll)
                {
                    try
                    {
                        if (current.Path is null) throw new CodexUnavailable("runtime_missing");
                        if (client is null || selectedPath != current.Path)
                        {
                            if (client is not null) await client.DisposeAsync();
                            client = null;
                            if (!RuntimeTrust.IsOpenAI(current.Path)) throw new CodexUnavailable("runtime_missing");
                            client = await CodexRpc.StartAsync(current.Path, cancellationToken);
                            selectedPath = current.Path;
                        }
                        await ReadAsync(client, cancellationToken);
                        failures = 0;
                        nextPoll = DueAfter(60 + Random.Shared.NextDouble() * 5);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        SetFailure(error is CodexUnavailable unavailable ? unavailable.Status : "error");
                        // Only an error category is logged; runtime stderr, account data and
                        // upstream error messages may contain sensitive account information.
                        if (failures == 0) Log.Write("Codex usage reader unavailable: " + (error is CodexUnavailable reason ? reason.Status : error.GetType().Name));
                        if (client is not null) { await client.DisposeAsync(); client = null; }
                        failures = Math.Min(failures + 1, 4);
                        nextPoll = DueAfter(Math.Min(60 * Math.Pow(2, failures), 900));
                    }
                }
                await Task.Delay(1000, cancellationToken);
            }
        }
        finally { if (client is not null) await client.DisposeAsync(); }
    }

    private static long DueAfter(double seconds) => Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);

    private async Task ReadAsync(CodexRpc client, CancellationToken cancellationToken)
    {
        DiagnosticStage = "account_read";
        var accountResult = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
        if (!CodexContract.Object(accountResult, "account", out var account) || CodexContract.String(account, "type") != "chatgpt")
        {
            lock (gate) { value = CodexStats.Empty(true, "sign_in"); capturedAt = 0; refreshFailed = false; accountFingerprint = null; }
            return;
        }
        var plan = CodexContract.String(account, "planType");
        // Compare a one-way in-memory identity marker solely to discard another
        // account's cached windows. Never send or persist this marker or email.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((CodexContract.String(account, "email") ?? "") + "|" + plan)));
        lock (gate)
        {
            if (accountFingerprint != fingerprint) { value = CodexStats.Empty(true, "waiting"); capturedAt = 0; refreshFailed = false; }
            accountFingerprint = fingerprint;
        }
        DiagnosticStage = "limits_read";
        var limits = await client.RequestAsync("account/rateLimits/read", null, cancellationToken);
        var normalized = CodexContract.Normalize(limits, plan);
        lock (gate) { value = normalized; capturedAt = Stopwatch.GetTimestamp(); refreshFailed = false; }
        DiagnosticStage = "complete";
    }

    private void SetFailure(string status)
    {
        lock (gate)
        {
            if (status == "sign_in") { value = CodexStats.Empty(desktop.Running, status); capturedAt = 0; accountFingerprint = null; }
            else if (!value.Available) value = CodexStats.Empty(desktop.Running, status);
            refreshFailed = true;
        }
    }

    public async Task<CodexStats> DiagnoseAsync(CancellationToken cancellationToken)
    {
        desktop = DesktopRuntime.FindRunning();
        var runtime = desktop.Path ?? DesktopRuntime.FindInstalled();
        DiagnosticStage = "runtime_trust";
        if (runtime is null || !RuntimeTrust.IsOpenAI(runtime)) { SetFailure("runtime_missing"); return Snapshot; }
        try
        {
            DiagnosticStage = "runtime_start_initialize";
            await using var client = await CodexRpc.StartAsync(runtime, cancellationToken);
            await ReadAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            DiagnosticError = error is Win32Exception native ? "win32_" + native.NativeErrorCode : error is CodexUnavailable rpc ? "rpc_" + rpc.Code + rpc.Detail : error.GetType().Name;
            SetFailure(error is CodexUnavailable unavailable ? unavailable.Status : "error");
        }
        return Snapshot;
    }
}

internal sealed record DesktopRuntime(bool Running, string? Path)
{
    private static string WindowsApps => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    public static DesktopRuntime FindRunning()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (var name in new[] { "ChatGPT", "Codex" })
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (process.SessionId != self.SessionId) continue;
                            var image = process.MainModule?.FileName;
                            if (!CodexContract.IsDesktopImage(image, WindowsApps)) continue;
                            var runtime = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(image!)!, "resources", "codex.exe");
                            return new(true, File.Exists(runtime) ? runtime : null);
                        }
                        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
                    }
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
        return new(false, null);
    }

    public static string? FindInstalled()
    {
        try
        {
            var package = new PackageManager().FindPackagesForUser("")
                .Where(p => p.Id.Name is "OpenAI.Codex" or "OpenAI.ChatGPT")
                .OrderByDescending(p => p.Id.Version.Major).ThenByDescending(p => p.Id.Version.Minor).ThenByDescending(p => p.Id.Version.Build).ThenByDescending(p => p.Id.Version.Revision);
            foreach (var item in package)
            {
                var runtime = System.IO.Path.Combine(item.InstalledLocation.Path, "app", "resources", "codex.exe");
                if (File.Exists(runtime)) return runtime;
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException) { }
        return null;
    }
}

internal sealed class CodexUnavailable(string status, int code = 0, string detail = "") : IOException("Codex usage unavailable.")
{
    public string Status { get; } = status;
    public int Code { get; } = code;
    public string Detail { get; } = detail;
}

// Strictly owned, hidden stdio process. There is no network listener, PATH lookup,
// credential-file access, login ceremony, thread creation or inference request.
internal sealed class CodexRpc : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task outputPump, errorPump;
    private int sequence;
    private string stderrCategory = "";
    private CodexRpc(Process process)
    {
        this.process = process;
        outputPump = PumpAsync();
        errorPump = DiscardErrorsAsync();
    }

    public static async Task<CodexRpc> StartAsync(string runtime, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(runtime)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Configuration.DirectoryPath
        };
        Directory.CreateDirectory(start.WorkingDirectory);
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        // Inherit the user's normal Codex home/auth resolution. Never change or copy auth.
        var process = Process.Start(start) ?? throw new CodexUnavailable("runtime_missing");
        var client = new CodexRpc(process);
        try
        {
            await client.RequestAsync("initialize", new { clientInfo = new { name = "buddyos_usage", title = "ClawPal usage display", version = "0.6.2" }, capabilities = new { } }, cancellationToken);
            await client.process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), cancellationToken);
            await client.process.StandardInput.FlushAsync(cancellationToken);
            return client;
        }
        catch (Exception error)
        {
            await client.DisposeAsync();
            if (error is OperationCanceledException) throw;
            throw new CodexUnavailable("error", client.exitCode, "_" + error.GetType().Name + client.stderrCategory);
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (method is not ("initialize" or "account/read" or "account/rateLimits/read" or "thread/list")) throw new InvalidOperationException("Unsupported read-only method.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        int id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            var request = parameters is null ? JsonSerializer.Serialize(new { method, id }) : JsonSerializer.Serialize(new { method, id, @params = parameters });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(stopping.Token) is { } line)
            {
                if (line.Length > 1_048_576) throw new IOException("Runtime response too large.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using var scope = document;
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number)) continue;
                // No host authentication handoff is implemented. Unexpected server requests
                // cannot be mistaken for replies or induce credential access.
                if (message.TryGetProperty("method", out _)) throw new CodexUnavailable("unavailable");
                if (!pending.TryGetValue(number, out var completion)) continue;
                if (message.TryGetProperty("error", out var error))
                {
                    var text = CodexContract.String(error, "message") ?? "";
                    var auth = text.Contains("401", StringComparison.OrdinalIgnoreCase) || text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("sign in", StringComparison.OrdinalIgnoreCase);
                    var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var errorCode) && errorCode.TryGetInt32(out var parsedCode) ? parsedCode : 0;
                    completion.TrySetException(new CodexUnavailable(auth ? "sign_in" : "error", code));
                }
                else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                else completion.TrySetException(new CodexUnavailable("error"));
            }
        }
        catch (Exception error)
        {
            foreach (var completion in pending.Values) completion.TrySetException(error is CodexUnavailable known ? known : new CodexUnavailable("error"));
        }
        finally
        {
            foreach (var completion in pending.Values) completion.TrySetException(new CodexUnavailable("error"));
        }
    }

    private async Task DiscardErrorsAsync()
    {
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), stopping.Token)) > 0)
            {
                var text = new string(buffer, 0, count);
                // Classify known startup failures without retaining any stderr text.
                foreach (var marker in new[] { "unexpected argument", "permission denied", "access is denied", "configuration", "keyring", "authentication", "not found", "os error" })
                    if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) stderrCategory = "_" + marker.Replace(' ', '_');
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private int exitCode;
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try { process.StandardInput.Close(); } catch (IOException) { }
        try
        {
            if (!process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
        try { await Task.WhenAll(outputPump, errorPump); } catch (OperationCanceledException) { }
        try { if (process.HasExited) exitCode = process.ExitCode; } catch (InvalidOperationException) { }
        process.Dispose(); stopping.Dispose();
    }
}

internal static class RuntimeTrust
{
    private static readonly Guid Action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint StateAction; public IntPtr StateData, Url; public uint Flags, UiContext;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, ref TrustData data);

    public static bool IsOpenAI(string path)
    {
        if (!System.IO.File.Exists(path)) return false;
        var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer, StateAction = 1, Flags = 0x1000 };
        var action = Action;
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return certificate.GetNameInfo(X509NameType.SimpleName, false) == "OpenAI OpCo, LLC";
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfo>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }
}
