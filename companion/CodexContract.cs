using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawPal.Companion;

public sealed record CodexWindow(bool Available, double RemainingPercent, int WindowMinutes, string ResetText, long ResetsAt = 0)
{
    public static readonly CodexWindow Empty = new(false, 0, 0, "");
}

public sealed record CodexStats(bool Running, bool Available, bool Stale, string Status, string Plan, long AgeS,
    CodexWindow Primary, CodexWindow Secondary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CodexActivityStats? Activity = null)
{
    public static CodexStats Empty(bool running, string status) => new(running, false, false, status, "", 0, CodexWindow.Empty, CodexWindow.Empty);
}

// The account response is reduced in memory. Account identifiers, email, credentials,
// raw RPC payloads and other buckets never enter telemetry or logs.
public static class CodexContract
{
    public static CodexStats Normalize(JsonElement result, string? plan, TimeZoneInfo? zone = null)
    {
        JsonElement bucket = default;
        if (Object(result, "rateLimitsByLimitId", out var buckets) && Object(buckets, "codex", out var current)) bucket = current;
        else if (Object(result, "rateLimits", out var legacy) && String(legacy, "limitId") == "codex") bucket = legacy;
        if (bucket.ValueKind != JsonValueKind.Object) return CodexStats.Empty(true, "no_limits") with { Plan = MusicContract.LimitText(plan, 31) };
        var primary = Window(bucket, "primary", zone ?? TimeZoneInfo.Local);
        var secondary = Window(bucket, "secondary", zone ?? TimeZoneInfo.Local);
        var available = primary.Available || secondary.Available;
        return new(true, available, false, available ? "live" : "no_limits",
            MusicContract.LimitText(String(bucket, "planType") ?? plan, 31), 0, primary, secondary);
    }

    private static CodexWindow Window(JsonElement bucket, string name, TimeZoneInfo zone)
    {
        if (!Object(bucket, name, out var window) || !window.TryGetProperty("usedPercent", out var used) ||
            used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percentage) || !double.IsFinite(percentage)) return CodexWindow.Empty;
        int minutes = 0;
        if (window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var value) && value is >= 0 and <= 525600) minutes = value;
        string reset = ""; long resetAt=0;
        if (window.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds) && seconds is > 0 and <= uint.MaxValue)
        {
            try { reset = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), zone).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture); resetAt=seconds; }
            catch (ArgumentException) { }
        }
        return new(true, Math.Round(Math.Clamp(100 - percentage, 0, 100), 1), minutes, MusicContract.LimitText(reset, 47),resetAt);
    }

    public static bool Object(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }
    public static string? String(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool IsDesktopImage(string? path, string windowsApps)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(windowsApps).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar);
            if (parts.Length != 3 || !parts[1].Equals("app", StringComparison.OrdinalIgnoreCase)) return false;
            return (parts[0].StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("OpenAI.ChatGPT_", StringComparison.OrdinalIgnoreCase)) &&
                parts[0].EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase) &&
                (parts[2].Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("Codex.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
