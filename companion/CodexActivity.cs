using System.Diagnostics;
using System.Text.Json;

namespace ClawPal.Companion;

// A separate local worker keeps task-file I/O and metadata polling independent
// from both the quota request cadence and one-second device telemetry.
public sealed class CodexActivity
{
    public Func<bool> Enabled { get; init; } = () => true;
    private readonly object gate = new();
    private readonly Dictionary<string, RolloutTail> tails = new(StringComparer.Ordinal);
    private readonly string sessionsRoot = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");
    private CodexActivityStats snapshot = CodexActivityStats.Empty("waiting");
    private CodexActivityItem[] allItems = [];
    private long capturedAt;
    private bool metadataFailed;
    private bool metadataPartial;
    private bool running;
    public string DiagnosticError { get; private set; } = "";

    public CodexActivityStats Snapshot
    {
        get
        {
            lock (gate)
            {
                var age = capturedAt == 0 ? 0 : (long)Math.Clamp(Stopwatch.GetElapsedTime(capturedAt).TotalSeconds, 0, 31536000);
                var items = allItems.Select(item =>
                {
                    var itemAge = Math.Min(31536000, item.AgeS + age);
                    return item with { AgeS = itemAge, State = item.State == "working" && itemAge > 120 ? "unknown" : item.State };
                }).ToArray();
                return snapshot with
                {
                    AgeS = age,
                    Stale = snapshot.Stale || !running || age > 30,
                    Status = running ? snapshot.Status : "closed",
                    WorkingCount = items.Count(item => item.State == "working"),
                    Items = items.OrderByDescending(item => item.State == "working").ThenBy(item => item.AgeS).Take(3).ToArray()
                };
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        CodexRpc? client = null;
        string? runtime = null;
        long nextMetadata = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var desktop = Enabled() ? DesktopRuntime.FindRunning() : new DesktopRuntime(false,null);
                lock (gate) running = desktop.Running;
                if (!desktop.Running)
                {
                    if (client is not null) { await client.DisposeAsync(); client = null; }
                    runtime = null;
                    nextMetadata = 0;
                }
                else
                {
                    try
                    {
                        if (nextMetadata == 0 || Stopwatch.GetTimestamp() >= nextMetadata)
                        {
                            // Set the next attempt before awaiting, including failed attempts.
                            nextMetadata = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
                            if (desktop.Path is null) throw new CodexUnavailable("runtime_missing");
                            if (client is null || runtime != desktop.Path)
                            {
                                if (client is not null) await client.DisposeAsync();
                                client = null;
                                if (!RuntimeTrust.IsOpenAI(desktop.Path)) throw new CodexUnavailable("runtime_missing");
                                client = await CodexRpc.StartAsync(desktop.Path, cancellationToken);
                                runtime = desktop.Path;
                            }
                            await RefreshMetadataAsync(client, cancellationToken);
                        }
                        await RefreshTailsAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        Fail(error);
                        if (client is not null) { await client.DisposeAsync(); client = null; }
                    }
                }
                await Task.Delay(2000, cancellationToken);
            }
        }
        finally { if (client is not null) await client.DisposeAsync(); }
    }

    private async Task RefreshMetadataAsync(CodexRpc client, CancellationToken cancellationToken)
    {
        var result = await client.RequestAsync("thread/list", new { limit = ActivityContract.MaxCandidates, sortKey = "updated_at", sourceKinds = new[] { "vscode", "appServer" }, archived = false, useStateDbOnly = true }, cancellationToken);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new IOException("Invalid task metadata shape.");
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var thread in data.EnumerateArray().Take(ActivityContract.MaxCandidates))
        {
            if (thread.ValueKind != JsonValueKind.Object || ActivityContract.Metadata(thread, sessionsRoot) is not { } task) continue;
            current.Add(task.Id);
            if (!tails.TryGetValue(task.Id, out var tail) || tail.Metadata.RolloutPath != task.RolloutPath) tails[task.Id] = new(task, sessionsRoot);
            else tail.Metadata = task;
        }
        foreach (var key in tails.Keys.Where(key => !current.Contains(key)).ToArray()) tails.Remove(key);
        metadataFailed = false;
        metadataPartial = !string.IsNullOrEmpty(CodexContract.String(result, "nextCursor"));
        lock (gate) snapshot = snapshot with { Available = true };
    }

    private async Task RefreshTailsAsync(CancellationToken cancellationToken)
    {
        bool partial = metadataPartial;
        foreach (var tail in tails.Values)
        {
            try { await tail.ReadAsync(cancellationToken); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                partial = true;
                tail.ReadFailed = true;
            }
        }
        var now = DateTimeOffset.UtcNow;
        var items = tails.Values.Select(tail => tail.ReadFailed
            ? new CodexActivityItem(tail.Metadata.Title, tail.Metadata.Project, "unknown", 31536000)
            : tail.Observation.Item(tail.Metadata, now)).ToArray();
        partial |= tails.Values.Any(tail => !tail.Observation.HasBoundary);
        lock (gate)
        {
            if (!snapshot.Available) return;
            allItems = items;
            capturedAt = Stopwatch.GetTimestamp();
            snapshot = new(true, metadataFailed || tails.Values.Any(tail => tail.ReadFailed),
                metadataFailed || partial ? "partial" : "local", 0, items.Count(item => item.State == "working"), items.Length, []);
        }
    }

    private void Fail(Exception error)
    {
        DiagnosticError = error is CodexUnavailable rpc ? "rpc_" + rpc.Code : error.GetType().Name;
        if (!metadataFailed) Log.Write("Codex activity unavailable: " + DiagnosticError);
        metadataFailed = true;
        lock (gate) snapshot = snapshot with { Stale = true, Status = snapshot.Available ? "partial" : "error" };
    }

    public async Task<CodexActivityStats> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var desktop = Enabled() ? DesktopRuntime.FindRunning() : new DesktopRuntime(false,null);
        running = desktop.Running;
        var runtime = desktop.Path ?? DesktopRuntime.FindInstalled();
        if (runtime is null || !RuntimeTrust.IsOpenAI(runtime)) { snapshot = CodexActivityStats.Empty("unavailable"); return Snapshot; }
        try
        {
            await using var client = await CodexRpc.StartAsync(runtime, cancellationToken);
            await RefreshMetadataAsync(client, cancellationToken);
            await RefreshTailsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Fail(error); }
        return Snapshot;
    }
}

internal sealed class RolloutTail(LocalTaskMetadata metadata, string sessionsRoot)
{
    public LocalTaskMetadata Metadata { get; set; } = metadata;
    public ActivityObservation Observation { get; private set; } = new();
    public bool ReadFailed { get; set; }
    private ActivityLines lines = new();
    private long offset;
    private bool initialized;

    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        // Enforce the metadata path boundary again at use time, rejecting junctions
        // and symlinks so a rollout path cannot redirect the reader to other files.
        var file = new FileInfo(Metadata.RolloutPath);
        if (!ActivityContract.IsSessionPath(file.FullName, sessionsRoot)) throw new IOException("Task path outside sessions.");
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked task file unavailable.");
        var root = Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar);
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked task directory unavailable.");
            if (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (!initialized || length < offset || length - offset > ActivityContract.MaxTailBytes)
        {
            offset = Math.Max(0, length - ActivityContract.MaxTailBytes);
            Observation = new();
            lines = new(offset > 0);
            initialized = true;
        }
        stream.Position = offset;
        var remaining = (int)Math.Min(ActivityContract.MaxTailBytes, length - offset);
        var buffer = new byte[Math.Min(65536, remaining)];
        while (remaining > 0)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) break;
            lines.Feed(buffer.AsSpan(0, count), Observation);
            offset += count;
            remaining -= count;
        }
        ReadFailed = false;
    }
}
