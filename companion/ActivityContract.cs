using System.Globalization;
using System.Text.Json;

namespace ClawPal.Companion;

public sealed record CodexActivityItem(string Title, string Project, string State, long AgeS);
public sealed record CodexActivityStats(bool Available, bool Stale, string Status, long AgeS, int WorkingCount, int TotalCount, CodexActivityItem[] Items)
{
    public static CodexActivityStats Empty(string status) => new(false, false, status, 0, 0, 0, []);
}

public sealed record LocalTaskMetadata(string Id, string Title, string Project, string RolloutPath, long UpdatedAt);

public static class ActivityContract
{
    public const int MaxCandidates = 32;
    public const int MaxTailBytes = 1_048_576;
    public static long Age(DateTimeOffset timestamp, DateTimeOffset now) => (long)Math.Clamp((now - timestamp).TotalSeconds, 0, 31536000);

    public static LocalTaskMetadata? Metadata(JsonElement thread, string sessionsRoot)
    {
        // The local desktop currently records vscode as its source. Require its
        // originator as well; ordinary VS Code, CLI, subagents and cloud are excluded.
        if (CodexContract.String(thread, "originator") != "Codex Desktop" ||
            CodexContract.String(thread, "source") is not ("vscode" or "appServer" or "app-server") ||
            (thread.TryGetProperty("parentThreadId", out var parent) && parent.ValueKind != JsonValueKind.Null) ||
            (thread.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)) return null;
        var id = CodexContract.String(thread, "id");
        var path = CodexContract.String(thread, "path");
        if (id is null || !Guid.TryParse(id, out _) || path is null || !IsSessionPath(path, sessionsRoot)) return null;
        var title = CodexContract.String(thread, "name");
        // Never fall back to preview: that field is usually a user's first prompt.
        title = string.IsNullOrWhiteSpace(title) ? "" : title;
        var cwd = CodexContract.String(thread, "cwd") ?? "";
        string project;
        try { project = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch (ArgumentException) { project = ""; }
        long updated = 0;
        if (thread.TryGetProperty("updatedAt", out var at) && at.ValueKind == JsonValueKind.Number) at.TryGetInt64(out updated);
        return new(id, MusicContract.LimitText(title, 63), MusicContract.LimitText(project, 31), Path.GetFullPath(path), updated);
    }

    public static bool IsSessionPath(string path, string sessionsRoot)
    {
        try
        {
            var root = Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(full).Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

// Only lifecycle markers, turn IDs and timestamps are retained from each JSONL
// record. Items, messages, tool output, context and prompts are discarded.
public sealed class ActivityObservation
{
    private string? turnId;
    private string boundary = "unknown";
    public DateTimeOffset? LastActivity { get; private set; }
    public bool HasBoundary => boundary != "unknown";

    public void Observe(ReadOnlyMemory<byte> line)
    {
        if (line.Length == 0 || line.Length > ActivityContract.MaxTailBytes) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (CodexContract.String(row, "type") != "event_msg" || !CodexContract.Object(row, "payload", out var payload) ||
                !DateTimeOffset.TryParse(CodexContract.String(row, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return;
            // Bad future or ancient timestamps cannot create a perpetual working state.
            if (at < DateTimeOffset.UnixEpoch || at > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            LastActivity = LastActivity is { } previous && previous > at ? previous : at;
            var kind = CodexContract.String(payload, "type");
            var id = CodexContract.String(payload, "turn_id");
            if (kind is "task_started" or "turn_started")
            {
                if (string.IsNullOrEmpty(id)) return;
                turnId = id; boundary = "working";
            }
            else if (kind is "task_complete" or "turn_complete" or "turn_aborted")
            {
                if (turnId is not null && id is not null && turnId != id) return;
                boundary = kind == "turn_aborted" ? "interrupted" : "completed";
                turnId = id;
            }
        }
        catch (JsonException) { }
    }

    public CodexActivityItem Item(LocalTaskMetadata metadata, DateTimeOffset now)
    {
        var age = LastActivity is { } last ? ActivityContract.Age(last, now) : 31536000;
        var state = boundary;
        if (state == "working" && age > 120) state = "unknown";
        else if (state == "unknown" && LastActivity is not null && age <= 120) state = "recent";
        return new(metadata.Title, metadata.Project, state, age);
    }
}

// A bounded streaming line accumulator permits partial UTF-8 records across reads.
// Oversized records are discarded through their newline without retaining content.
public sealed class ActivityLines
{
    private byte[] pending = [];
    private bool discardPrefix;
    public ActivityLines(bool discardPrefix = false) { this.discardPrefix = discardPrefix; }
    public void Feed(ReadOnlySpan<byte> bytes, ActivityObservation observation)
    {
        var combined = new byte[pending.Length + bytes.Length];
        pending.CopyTo(combined, 0); bytes.CopyTo(combined.AsSpan(pending.Length)); pending = [];
        int start = 0;
        for (int i = 0; i < combined.Length; i++)
        {
            if (combined[i] != (byte)'\n') continue;
            if (!discardPrefix && i - start <= ActivityContract.MaxTailBytes) observation.Observe(combined.AsMemory(start, i - start));
            discardPrefix = false;
            start = i + 1;
        }
        if (combined.Length - start > ActivityContract.MaxTailBytes) discardPrefix = true;
        else if (!discardPrefix) pending = combined.AsSpan(start).ToArray();
    }
}
