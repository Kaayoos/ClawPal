using System.Text.Json;

namespace ClawPal.Companion;

internal sealed record ClaudeDesktopReading(string SessionId, DateTimeOffset CapturedAt,
    CodexWindow Primary, CodexWindow Secondary);

// Read-only adapter for Desktop's locally saved plan-usage history. This is an
// observed Desktop file format, not a public API. Never read cookies, decrypt
// login data, poll a private endpoint, or estimate plan usage from token counts.
internal static class ClaudeDesktop
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);
    private const int MaxFileBytes = 1_048_576;

    internal static ClaudeDesktopReading? Read(IEnumerable<ClaudeSession> sessions, DateTimeOffset now,
        Func<ClaudeSession, bool> alive)
    {
        var latest = LatestLive(sessions, now, alive);
        if (latest is null) return null;
        foreach (string root in Roots())
        {
            try
            {
                var reading = ReadAt(root, latest, now);
                if (reading is not null) return reading;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return null;
    }

    internal static ClaudeSession? LatestLive(IEnumerable<ClaudeSession> sessions, DateTimeOffset now,
        Func<ClaudeSession, bool> alive) => sessions.Where(s => !s.Ended && s.Seen <= now.AddMinutes(1) &&
            s.Seen > now.AddHours(-24) && alive(s)).OrderByDescending(s => s.Seen).FirstOrDefault();

    private static IEnumerable<string> Roots()
    {
        var roots = new List<string> { Path.Combine(WindowsFolders.RoamingAppData, "Claude") };
        string packages = Path.Combine(WindowsFolders.LegacyLocalAppData, "Packages");
        try
        {
            if (Directory.Exists(packages))
                roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Take(16)
                    .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static ClaudeDesktopReading? ReadAt(string root, ClaudeSession latest, DateTimeOffset now)
    {
        // Only inspect the account marker. Other configuration properties, which
        // may include encrypted authentication data, are never used or retained.
        using var config = ReadJson(Path.Combine(root, "config.json"));
        if (config is null || !Guid.TryParse(CodexContract.String(config.RootElement, "lastKnownAccountUuid"), out var account)) return null;
        string accountDirectory = Path.Combine(root, "claude-code-sessions", account.ToString("D"));
        if (!Directory.Exists(accountDirectory)) return null;
        string? organization = null;
        foreach (string directory in Directory.EnumerateDirectories(accountDirectory).Take(16))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out var org)) continue;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("local_*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(256))
            {
                try
                {
                    using var session = ReadJson(file.FullName, 262_144);
                    if (session is null || !MatchesSession(session.RootElement, latest.Id)) continue;
                    string matched = org.ToString("D");
                    if (organization is not null && organization != matched) return null;
                    organization = matched;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        if (organization is null) return null;
        // A known Desktop session with missing/invalid history must not inherit
        // another live CLI account's limits. Return an explicitly empty reading.
        var empty = new ClaudeDesktopReading(latest.Id, DateTimeOffset.UnixEpoch, CodexWindow.Empty, CodexWindow.Empty);
        try
        {
            using var history = ReadJson(Path.Combine(root, "plan-usage-history.json"));
            return history is null ? empty : ParseHistory(history.RootElement, organization, latest.Id, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return empty; }
    }

    internal static bool MatchesSession(JsonElement root, string id) =>
        CodexContract.String(root, "cliSessionId") == id && root.TryGetProperty("isArchived", out var archived) &&
        archived.ValueKind == JsonValueKind.False;

    internal static ClaudeDesktopReading ParseHistory(JsonElement root, string organization, string sessionId, DateTimeOffset now)
    {
        var empty = new ClaudeDesktopReading(sessionId, DateTimeOffset.UnixEpoch, CodexWindow.Empty, CodexWindow.Empty);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 2 ||
            !root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array ||
            samples.GetArrayLength() > 20_000) return empty;
        JsonElement selected = default;
        long newest = long.MinValue;
        foreach (var sample in samples.EnumerateArray())
        {
            if (!Guid.TryParse(CodexContract.String(sample, "org"), out var org) ||
                !org.ToString("D").Equals(organization, StringComparison.OrdinalIgnoreCase) ||
                !sample.TryGetProperty("t", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number ||
                !timestamp.TryGetInt64(out long at) || at < newest) continue;
            newest = at; selected = sample;
        }
        // Check the newest sample before values, so an empty/new account reading
        // or future-dated/corrupt sample cannot resurrect an older valid quota.
        if (selected.ValueKind != JsonValueKind.Object || newest > now.AddMinutes(1).ToUnixTimeMilliseconds() ||
            newest < (now - MaximumAge).ToUnixTimeMilliseconds() ||
            !CodexContract.Object(selected, "u", out var usage)) return empty;
        return new(sessionId, DateTimeOffset.FromUnixTimeMilliseconds(newest),
            Window(usage, "fh", 300), Window(usage, "sd", 10080));
    }

    private static CodexWindow Window(JsonElement root, string key, int minutes)
    {
        if (!root.TryGetProperty(key, out var used) || used.ValueKind != JsonValueKind.Number ||
            !used.TryGetDouble(out double value) || !double.IsFinite(value) || value < 0 || value > 100) return CodexWindow.Empty;
        // The history intentionally omits reset timestamps. Do not invent one.
        return new(true, Math.Round(100 - value, 1), minutes, "", 0);
    }

    internal static CodexStats Merge(CodexStats hooks, ClaudeDesktopReading? reading, DateTimeOffset now)
    {
        if (reading is null) return hooks;
        bool timely = reading.CapturedAt <= now.AddMinutes(1) && now - reading.CapturedAt <= MaximumAge;
        var primary = timely ? reading.Primary : CodexWindow.Empty;
        var secondary = timely ? reading.Secondary : CodexWindow.Empty;
        bool available = primary.Available || secondary.Available;
        return hooks with { Available = available, Stale = available, Status = available ? "cached" : "no_limits",
            Plan = "", AgeS = available ? ActivityContract.Age(reading.CapturedAt, now) : 0,
            Primary = primary, Secondary = secondary };
    }

    private static JsonDocument? ReadJson(string path, int maximumBytes = MaxFileBytes)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length <= 0 || length > maximumBytes) return null;
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }
}
