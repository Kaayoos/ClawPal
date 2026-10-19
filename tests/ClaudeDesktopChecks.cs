using ClawPal.Companion;
using System.Text.Json;

internal static class ClaudeDesktopChecks
{
    internal static int Run(string directory)
    {
        int passed = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS: Claude Desktop " + message); }
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        const string account = "10000000-0000-0000-0000-000000000001";
        const string org = "20000000-0000-0000-0000-000000000002";
        const string other = "30000000-0000-0000-0000-000000000003";
        string Sample(string values, string organization = org, long? at = null) =>
            "{\"org\":\"" + organization + "\",\"t\":" + (at ?? now.AddMinutes(-5).ToUnixTimeMilliseconds()) + ",\"u\":" + values + "}";
        string History(params string[] rows) => "{\"version\":2,\"samples\":[" + string.Join(',', rows) + "]}";
        ClaudeDesktopReading Parse(string text) { using var json = JsonDocument.Parse(text); return ClaudeDesktop.ParseHistory(json.RootElement, org, "desktop-session", now); }
        var reading = Parse(History(Sample("{\"fh\":8,\"sd\":1,\"xu\":99}")));
        Check(reading.Primary.RemainingPercent == 92 && reading.Secondary.RemainingPercent == 99, "uses plan percentages, not token or extra-spend counters");
        Check(reading.Primary.WindowMinutes == 300 && reading.Secondary.WindowMinutes == 10080, "preserves independent five-hour and weekly windows");
        Check(reading.Primary.ResetsAt == 0 && reading.Primary.ResetText == "", "does not invent reset times absent from Desktop history");
        var activity = new CodexActivityStats(true, false, "local", 0, 1, 1, [new("", "Example", "working", 0)]);
        var hooks = CodexStats.Empty(true, "no_limits") with { Activity = activity };
        var merged = ClaudeDesktop.Merge(hooks, reading, now);
        Check(merged.Available && merged.Stale && merged.Status == "cached" && merged.AgeS == 300, "cached readings carry their real sample age");
        Check(merged.Running && ReferenceEquals(merged.Activity, activity), "adding quota preserves hook activity and running state");
        var fresh = hooks with { Available = true, Stale = false, Primary = new(true, 81, 300, "", now.AddHours(1).ToUnixTimeSeconds()), AgeS = 10 };
        Check(ReferenceEquals(ClaudeDesktop.Merge(fresh, reading, now), fresh), "older Desktop history cannot overwrite fresh status-line limits");
        Check(ReferenceEquals(ClaudeDesktop.Merge(fresh, reading with { CapturedAt = DateTimeOffset.UnixEpoch }, now), fresh), "a missing Desktop history file cannot erase live status-line limits");
        Check(ReferenceEquals(ClaudeDesktop.Merge(hooks, null, now), hooks), "CLI state is unchanged when no Desktop session matches");
        Check(!ClaudeDesktop.Merge(hooks, reading, now.AddMinutes(26)).Available, "cached quota expires without a new Desktop sample");
        Check(!Parse(History(Sample("{\"fh\":8}", other))).Primary.Available, "another organization's quota is never borrowed");
        Check(Parse(History(Sample("{\"fh\":8}"), Sample("{\"fh\":90}", other, now.ToUnixTimeMilliseconds()))).Primary.RemainingPercent == 92, "newer unrelated samples cannot replace the matched organization");
        Check(Parse(History(Sample("{\"fh\":0,\"sd\":100}"))).Secondary.RemainingPercent == 0, "exhausted quota is available, not missing");
        Check(!Parse(History(Sample("{\"fh\":-1,\"sd\":101}"))).Primary.Available, "out-of-range values are rejected");
        Check(!Parse(History(Sample("{\"fh\":\"8\",\"sd\":null}"))).Primary.Available, "string and null values are not percentages");
        Check(!Parse(History(Sample("{\"fh\":8}"), Sample("{}", at: now.ToUnixTimeMilliseconds()))).Primary.Available, "empty newest sample clears old limits");
        Check(!Parse(History(Sample("{\"fh\":8}", at: now.AddMinutes(-31).ToUnixTimeMilliseconds()))).Primary.Available, "old samples are rejected");
        Check(!Parse(History(Sample("{\"fh\":8}"), Sample("{\"fh\":7}", at: now.AddDays(1).ToUnixTimeMilliseconds()))).Primary.Available, "future samples cannot revive older readings");
        Check(!Parse("{\"version\":1,\"samples\":[]}").Primary.Available, "unscoped legacy history fails closed");
        Check(!Parse("{\"version\":\"2\",\"samples\":[]}").Primary.Available, "wrongly typed schema fails closed");
        Check(!Parse("[]").Primary.Available, "unknown file formats fail closed");
        var row = new ClaudeSession("desktop-session", "", "Example", "working", now, now, 123, 456, false, CodexWindow.Empty, CodexWindow.Empty, DateTimeOffset.UnixEpoch);
        Check(ClaudeDesktop.LatestLive([row with { Ended = true }], now, _ => true) is null, "ended sessions cannot select Desktop quota");
        Check(ClaudeDesktop.LatestLive([row], now, _ => false) is null, "crashed sessions cannot select Desktop quota");
        Check(ClaudeDesktop.LatestLive([row, row with { Id = "cli", Seen = now.AddSeconds(1) }], now, _ => true)?.Id == "cli", "most recent live session selects the usage source");
        string root = Path.Combine(directory, "claude-desktop");
        string sessions = Path.Combine(root, "claude-code-sessions", account, org);
        Directory.CreateDirectory(sessions);
        string config = Path.Combine(root, "config.json"), session = Path.Combine(sessions, "local_example.json"), history = Path.Combine(root, "plan-usage-history.json");
        string privateConfig = JsonSerializer.Serialize(new { lastKnownAccountUuid = account, ignoredCredential = "never retained" });
        File.WriteAllText(config, privateConfig);
        File.WriteAllText(session, "{\"cliSessionId\":\"desktop-session\",\"isArchived\":false,\"promptAppendSnapshot\":\"private prompt\"}");
        File.WriteAllText(history, History(Sample("{\"fh\":8,\"sd\":1}")));
        var actual = ClaudeDesktop.ReadAt(root, row, now);
        Check(actual?.Primary.RemainingPercent == 92, "resolves usage through current-account Desktop session metadata");
        string wire = JsonSerializer.Serialize(actual);
        Check(!wire.Contains(account) && !wire.Contains(org) && !wire.Contains("private") && !wire.Contains("never retained"), "identity markers and unrelated metadata never enter the quota snapshot");
        Check(File.ReadAllText(config) == privateConfig, "Desktop settings are read-only");
        Check(ClaudeDesktop.ReadAt(root, row with { Id = "separate-cli" }, now) is null, "unrelated CLI session cannot inherit Desktop account usage");
        File.WriteAllText(config, JsonSerializer.Serialize(new { lastKnownAccountUuid = other }));
        Check(ClaudeDesktop.ReadAt(root, row, now) is null, "switching the Desktop account immediately excludes old account data");
        File.WriteAllText(config, JsonSerializer.Serialize(new { lastKnownAccountUuid = "../escape" }));
        Check(ClaudeDesktop.ReadAt(root, row, now) is null, "account paths require a valid UUID");
        File.WriteAllText(config, privateConfig);
        File.WriteAllText(history, "{broken");
        Check(ClaudeDesktop.ReadAt(root, row, now) is { Primary.Available: false }, "partially written history leaves task activity usable");
        File.WriteAllText(history, new string(' ', 1_048_577));
        Check(ClaudeDesktop.ReadAt(root, row, now) is { Primary.Available: false }, "oversized history is bounded");
        File.WriteAllText(session, "{\"cliSessionId\":\"desktop-session\",\"isArchived\":true}");
        Check(ClaudeDesktop.ReadAt(root, row, now) is null, "archived Desktop sessions cannot select quota");
        return passed;
    }
}
