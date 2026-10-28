using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClawPal.Companion;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
const string token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string nonce = "0123456789abcdef0123456789abcdef";
const string id = "001122334455";
var address = IPAddress.Parse("192.168.1.10");
byte[] Hello(string proof, string n = nonce, string device = id, int port = Protocol.MonitorPort) => JsonSerializer.SerializeToUtf8Bytes(new { type = "buddy.hello", v = 1, id = device, name = "ClawPal", port, nonce = n, proof });
var proof = Protocol.Proof(token, nonce, id);
Check(Protocol.ValidateHello(Hello(proof), address, token, nonce, id)?.Id == id, "authenticated discovery succeeds");
Check(Protocol.ValidateHello(Hello(new string('0', 64)), address, token, nonce, id) is null, "forged discovery rejected");
Check(Protocol.ValidateHello(Hello(proof), address, new string('b', 64), nonce, id) is null, "wrong pairing token rejected");
Check(Protocol.ValidateHello(Hello(proof, "fedcba9876543210fedcba9876543210"), address, token, nonce, id) is null, "replayed nonce rejected");
Check(Protocol.ValidateHello(Hello(proof), address, token, nonce, "000000000000") is null, "wrong device rejected");
Check(Protocol.ValidateHello(Hello(proof, port: 80), address, token, nonce, id) is null, "unexpected WebSocket port rejected");
Check(Protocol.ValidateHello(Hello(proof), IPAddress.Parse("8.8.8.8"), token, nonce, id) is null, "public sender rejected");
Check(Protocol.ValidateHello(Hello(proof.ToUpperInvariant()), address, token, nonce, id) is null, "noncanonical proof rejected");
foreach (var malformed in new[] { "{", "null", "[]", "{}", "{\"type\":5}", "{\"type\":\"buddy.hello\",\"v\":\"bad\"}", "{\"type\":\"buddy.hello\",\"v\":999999999999999999}" })
    Check(Protocol.ValidateHello(Encoding.UTF8.GetBytes(malformed), address, token, nonce, id) is null, "malformed input rejected: " + malformed);
Check(Protocol.ValidateHello(new byte[2048], address, token, nonce, id) is null, "oversized hello rejected");
Check(Protocol.NewNonce() != Protocol.NewNonce() && Protocol.IsLowerHex(Protocol.NewNonce(), 32), "nonce fresh and canonical");
Check(Encoding.ASCII.GetString(Protocol.DiscoveryRequest(nonce)) == "BUDDYOS_DISCOVER_V1 " + nonce, "wire discovery request correct");
Check(CounterMath.CpuUsage(20, 50, 50, 70, 125, 75) == 50, "CPU delta includes kernel idle correctly");
Check(CounterMath.CpuUsage(0, 0, 0, 100, 100, 0) == 0, "idle CPU returns zero");
Check(CounterMath.CpuUsage(50, 100, 100, 50, 100, 100) is null, "zero time delta unavailable");
Check(CounterMath.CpuUsage(50, 100, 100, 20, 50, 50) is null, "counter reset unavailable");
Check(CounterMath.BytesPerSecond(100, 2100, 2) == 1000, "network bytes per second correct");
Check(CounterMath.BytesPerSecond(100, 10, 1) == 0, "network counter reset handled");
Check(CounterMath.BytesPerSecond(100, 200, 0) == 0 && CounterMath.BytesPerSecond(100, 200, double.NaN) == 0, "invalid sample duration handled");
var sample = new PcStats("pc.stats", 1, 2, "PC", 123, new(50, null, "CPU"), new(null, null, null, null, "GPU"), new(8, 16, 50), new(20, 40), 100);
using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(sample, Protocol.Json));
Check(serialized.RootElement.GetProperty("gpu").GetProperty("vram_used_mb").ValueKind == JsonValueKind.Null, "unavailable sensor is null and snake_case");
Check(serialized.RootElement.GetProperty("network").GetProperty("rx_bps").GetDouble() == 20, "network contract correct");
Check(serialized.RootElement.GetProperty("uptime_s").TryGetUInt64(out var uptime) && uptime == 100, "uptime is serialized as whole seconds accepted by firmware uint64 parser");
foreach (var field in new[] { "seq", "timestamp", "uptime_s" })
    Check(serialized.RootElement.GetProperty(field).TryGetUInt64(out _), field + " is an unsigned integral JSON value");
Check(serialized.RootElement.GetProperty("cpu").GetProperty("temperature").ValueKind == JsonValueKind.Null, "unavailable CPU temperature uses firmware-supported null");
Check(!serialized.RootElement.TryGetProperty("auto_open_apps", out _), "legacy telemetry omits the optional navigation preference");
foreach (bool autoOpen in new[] { false, true }) {
    var navigationSample = sample with {
        AutoOpenApps = autoOpen,
        Music = new(true, true, "Still playing", "Artist", "Album", true, 1, 100, true, true, true),
        Codex = CodexStats.Empty(true, "waiting")
    };
    using var navigation = JsonDocument.Parse(JsonSerializer.Serialize(navigationSample, Protocol.Json));
    Check(navigation.RootElement.GetProperty("auto_open_apps").GetBoolean() == autoOpen &&
        navigation.RootElement.GetProperty("music").GetProperty("running").GetBoolean() &&
        navigation.RootElement.GetProperty("music").GetProperty("title").GetString() == "Still playing" &&
        navigation.RootElement.GetProperty("codex").GetProperty("running").GetBoolean(),
        "navigation preference " + autoOpen + " preserves real Spotify and Codex status");
}
byte[] Ack(long sequence) => JsonSerializer.SerializeToUtf8Bytes(new { type = "buddy.ack", v = 1, seq = sequence });
Check(Protocol.ParseAcknowledgement(Ack(1)) == 1, "valid acknowledgement identifies delivered sequence");
Check(Protocol.ParseAcknowledgement(Encoding.UTF8.GetBytes("{\"type\":\"pc.stats\",\"v\":1,\"seq\":1}")) is null, "wrong acknowledgement type rejected");
Check(Protocol.ParseAcknowledgement(Encoding.UTF8.GetBytes("{\"type\":\"buddy.ack\",\"v\":2,\"seq\":1}")) is null, "wrong acknowledgement version rejected");
Check(Protocol.ParseAcknowledgement(Encoding.UTF8.GetBytes("{\"type\":\"buddy.ack\",\"v\":1,\"seq\":1.5}")) is null, "fractional acknowledgement sequence rejected");
var acknowledgements = new Acknowledgements();
acknowledgements.RecordSent(1);
Check(!acknowledgements.Accept(Ack(2)), "acknowledgement for unsent sequence rejected");
Check(acknowledgements.Accept(Ack(1)), "acknowledgement for outstanding sequence accepted");
Check(!acknowledgements.Accept(Ack(1)), "replayed acknowledgement cannot refresh watchdog");
var frames = new TextMessageBuffer();
var completeAck = Ack(1);
Check(frames.AddFrame(completeAck.AsSpan(0, 8), WebSocketMessageType.Text, false) is null, "incomplete acknowledgement waits for final frame");
var reassembled = frames.AddFrame(completeAck.AsSpan(8), WebSocketMessageType.Text, true);
Check(reassembled is not null && Protocol.ParseAcknowledgement(reassembled) == 1, "fragmented acknowledgement reassembles and validates");
bool FrameRejected(WebSocketMessageType type, int length)
{
    try { new TextMessageBuffer().AddFrame(new byte[length], type, true); return false; }
    catch (IOException) { return true; }
}
Check(FrameRejected(WebSocketMessageType.Close, 0), "close frame cannot be treated as connected success");
Check(FrameRejected(WebSocketMessageType.Binary, 1), "binary acknowledgement rejected");
Check(FrameRejected(WebSocketMessageType.Text, 4097), "oversized acknowledgement rejected");
byte[] Control(int id, string action) => JsonSerializer.SerializeToUtf8Bytes(new { type = "buddy.control", v = 1, id, action });
Check(Protocol.ParseControl(Control(1, "play_pause")) == new DeviceControl(1, "play_pause"), "allowlisted Spotify control parses");
Check(Protocol.ParseControl(Control(0, "next")) is null && Protocol.ParseControl(Control(-1, "next")) is null, "nonpositive control IDs rejected");
Check(Protocol.ParseControl(Control(1, "shell")) is null, "arbitrary control actions rejected");
foreach (var bad in new[] { "{\"type\":\"buddy.control\",\"v\":1,\"id\":2147483648,\"action\":\"next\"}", "{\"type\":\"buddy.control\",\"v\":1,\"id\":1,\"action\":\"next\",\"command\":\"calc\"}", "{\"type\":\"buddy.control\",\"v\":1,\"id\":1,\"id\":2,\"action\":\"next\"}" })
    Check(Protocol.ParseControl(Encoding.UTF8.GetBytes(bad)) is null, "invalid control shape/bounds rejected");
byte[] Valued(string action, object value) => JsonSerializer.SerializeToUtf8Bytes(new { type = "buddy.control", v = 1, id = 4, action, value });
Check(Protocol.ParseControl(Control(1, "shuffle")) == new DeviceControl(1, "shuffle") && Protocol.ParseControl(Control(1, "repeat")) is not null && Protocol.ParseControl(Control(1, "mute")) is not null, "shuffle, repeat and mute toggles parse");
Check(Protocol.ParseControl(Valued("seek", 95)) == new DeviceControl(4, "seek", 95) && Protocol.ParseControl(Valued("volume", 40)) == new DeviceControl(4, "volume", 40), "seek and volume carry their value");
Check(Protocol.ParseControl(Control(1, "seek")) is null && Protocol.ParseControl(Valued("next", 1)) is null && Protocol.ParseControl(Valued("volume", 101)) is null &&
    Protocol.ParseControl(Valued("seek", -1)) is null && Protocol.ParseControl(Valued("seek", 604801)) is null && Protocol.ParseControl(Valued("volume", "40")) is null &&
    Protocol.ParseControl(Valued("volume", 4.5)) is null, "missing, unexpected and out-of-range control values rejected");
byte[] Cover(params (byte R, byte G, byte B, int Count)[] parts) => parts.SelectMany(p => Enumerable.Repeat(new byte[] { p.B, p.G, p.R, 255 }, p.Count).SelectMany(pixel => pixel)).ToArray();
int vivid = MusicContract.AccentColor(Cover((200, 30, 40, 400), (40, 40, 200, 176)));
Check(vivid >> 16 > (vivid >> 8 & 255) && vivid >> 16 > (vivid & 255), "album accent follows the dominant vivid hue, not an average (" + vivid.ToString("x6") + ")");
Check(MusicContract.AccentColor(Cover((128, 128, 128, 576))) == 0 && MusicContract.AccentColor(Cover((8, 4, 6, 576))) == 0 && MusicContract.AccentColor([]) == 0, "grey, near-black and empty covers keep the display accent");
int lifted = MusicContract.AccentColor(Cover((40, 10, 90, 576)));
Check(lifted != 0 && ((lifted >> 16) * 0.2126 + (lifted >> 8 & 255) * 0.7152 + (lifted & 255) * 0.0722) / 255 > 0.3, "dark covers are lifted to stay readable on the dark display (" + lifted.ToString("x6") + ")");
var controlSequence = new ControlSequence();
Check(controlSequence.Accept(new(2, "next")) && !controlSequence.Accept(new(2, "next")) && !controlSequence.Accept(new(1, "previous")) && controlSequence.Accept(new(3, "previous")), "control IDs are monotonic with no replay");
Check(MusicContract.IsSpotifySession("Spotify.exe") && MusicContract.IsSpotifySession("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") && !MusicContract.IsSpotifySession("chrome.exe") && !MusicContract.IsSpotifySession("NotSpotify.exe"), "only Spotify session identities are selected");
var unicodeTitle = MusicContract.LimitText(string.Concat(Enumerable.Repeat("Żółw🦀", 40)), 191);
Check(Encoding.UTF8.GetByteCount(unicodeTitle) <= 191 && !unicodeTitle.Contains('\ufffd'), "music UTF-8 truncation preserves whole Unicode runes");
Check(MusicContract.LimitText("a\0b\nc\u0085d\u2028e\u2029f", 127) == "a b c d e f", "C0/C1 controls and Unicode separators sanitized for firmware strings");
Check(MusicContract.Seconds(12.9) == 12 && MusicContract.Seconds(-1) == 0 && MusicContract.Seconds(9999999) == 604800 && MusicContract.Seconds(double.NaN) == 0, "timeline values obey integer seven-day bounds");
var musicSample = sample with { Music = new(true, true, new string('"', 191), new string('\\', 127), new string('<', 127), true, 12, 200, true, true, false) };
var musicPayload = JsonSerializer.SerializeToUtf8Bytes(musicSample, Protocol.Json);
Check(musicPayload.Length <= 4096, "maximum escaped media fields fit device frame limit");
using var musicJson = JsonDocument.Parse(musicPayload);
Check(musicJson.RootElement.GetProperty("music").GetProperty("position_s").TryGetUInt64(out _) && musicJson.RootElement.GetProperty("music").GetProperty("control_id").GetInt32() == 0, "music telemetry uses integer seconds and initial control ID zero");
CodexStats Codex(string json) { using var doc = JsonDocument.Parse(json); return CodexContract.Normalize(doc.RootElement, "prolite", TimeZoneInfo.Utc); }
var weekly = Codex("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":86,"windowDurationMins":10080,"resetsAt":1730947200},"secondary":null}},"rateLimits":{"limitId":"codex","primary":{"usedPercent":1,"windowDurationMins":300}}}""");
Check(weekly.Available && weekly.Primary.RemainingPercent == 14 && weekly.Primary.WindowMinutes == 10080 && !weekly.Secondary.Available, "Codex map wins over legacy and primary duration is not assumed to be five hours");
Check(weekly.Primary.ResetText == "Thu 7 Nov, 02:40" && weekly.Plan == "prolite", "Codex reset uses supplied timezone with explicit English formatting");
Check(!Codex("""{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":20}}},"rateLimits":{"limitId":"other","primary":{"usedPercent":20}}}""").Available, "other usage buckets are never mislabeled as Codex");
Check(Codex("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300}}}""").Primary.RemainingPercent == 75, "legacy Codex bucket remains supported");
foreach (var missing in new[] { "null", "{}", "{\"usedPercent\":null}", "{\"usedPercent\":\"12\"}", "{\"usedPercent\":1e400}" })
    Check(!Codex("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":" + missing + "}}").Primary.Available, "missing or invalid used percentage remains unavailable");
var bounded = Codex("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":-25,"windowDurationMins":525601,"resetsAt":9223372036854775807},"secondary":{"usedPercent":150,"windowDurationMins":1.5}}}""");
Check(bounded.Primary.RemainingPercent == 100 && bounded.Secondary.RemainingPercent == 0 && bounded.Primary.WindowMinutes == 0 && bounded.Primary.ResetText == "" && bounded.Secondary.WindowMinutes == 0, "Codex percentage clamped and invalid time bounds suppressed");
var desktopRoot = Path.Combine(Path.GetTempPath(), "WindowsApps");
var appDirectory = Path.Combine(desktopRoot, "OpenAI.Codex_26.930.7945.0_x64__2p2nqsd0c76g0", "app");
Check(CodexContract.IsDesktopImage(Path.Combine(appDirectory, "ChatGPT.exe"), desktopRoot) && CodexContract.IsDesktopImage(Path.Combine(appDirectory, "Codex.exe"), desktopRoot), "known packaged desktop images detected");
Check(!CodexContract.IsDesktopImage(Path.Combine(appDirectory, "resources", "codex.exe"), desktopRoot) && !CodexContract.IsDesktopImage(Path.Combine(Path.GetTempPath(), "OpenAI", "Codex", "bin", "hash", "codex.exe"), desktopRoot), "own app-server and extracted CLI runtime cannot trigger desktop detection");
var maximumSample = musicSample with
{
    Seq = long.MaxValue, Timestamp = long.MaxValue, UptimeS = long.MaxValue, Hostname = new string('"', 63),
    Cpu = new(100, 120, new string('"', 95)), Gpu = new(100, 120, 99999999, 99999999, new string('"', 95)),
    Memory = new(99999999, 99999999, 100), Network = new(99999999999, 99999999999),
    Music = musicSample.Music! with { ControlId = int.MaxValue, ControlError = new string('"', 31), PositionS = 604800, DurationS = 604800, Repeat = 2, Volume = 100, ArtColor = 0xFFFFFF },
    Codex = new(true, true, true, new string('"', 23), new string('"', 31), 31536000,
        new(true, 100, 525600, new string('"', 47)), new(true, 100, 525600, new string('"', 47)))
};
var maximumPayload = JsonSerializer.SerializeToUtf8Bytes(maximumSample, Protocol.Json);
Check(maximumPayload.Length <= 4096, "all escaped maximum music/Codex fields plus PC metrics fit 4096-byte telemetry budget (" + maximumPayload.Length + " bytes)");
using var maximumJson = JsonDocument.Parse(maximumPayload);
Check(maximumJson.RootElement.GetProperty("codex").GetProperty("age_s").TryGetUInt32(out _) && maximumJson.RootElement.GetProperty("codex").GetProperty("primary").GetProperty("window_minutes").TryGetUInt32(out _), "Codex ages and durations serialize as integral firmware values");
var observationNow = DateTimeOffset.UtcNow;
byte[] ActivityEvent(string kind, DateTimeOffset at, string? turn = "turn-a") => JsonSerializer.SerializeToUtf8Bytes(new { type = "event_msg", timestamp = at.ToString("O"), payload = new { type = kind, turn_id = turn } });
var sessionsRoot = Path.Combine(Path.GetTempPath(), "buddy-activity-tests", "sessions");
var taskPath = Path.Combine(sessionsRoot, "2026", "10", "rollout.jsonl");
LocalTaskMetadata? TaskMetadata(string source = "vscode", string originator = "Codex Desktop", string? name = "Actual task title", string? parentId = null, string? path = null)
{
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "01234567-89ab-4cde-8fab-0123456789ab", source, originator, name, parentThreadId = parentId, path = path ?? taskPath, cwd = Path.Combine(Path.GetTempPath(), "Project"), preview = "PRIVATE PROMPT MUST NEVER BE TITLE", updatedAt = 123 }));
    return ActivityContract.Metadata(doc.RootElement, sessionsRoot);
}
var taskMetadata = TaskMetadata()!;
Check(taskMetadata.Title == "Actual task title" && taskMetadata.Project == "Project", "activity uses task metadata name and project basename");
Check(TaskMetadata(name: null)!.Title == "", "missing task title stays empty for display localization and never uses prompt preview");
Check(TaskMetadata(source: "subagent") is null && TaskMetadata(parentId: "parent") is null && TaskMetadata(originator: "VS Code") is null && TaskMetadata(source: "cloud") is null, "activity excludes subagents, other clients and cloud sources");
Check(TaskMetadata(path: Path.Combine(sessionsRoot, "..", "auth.json")) is null && !ActivityContract.IsSessionPath(Path.Combine(sessionsRoot + "-other", "rollout.jsonl"), sessionsRoot), "activity rollout path boundary rejects traversal and sibling prefix");
var observed = new ActivityObservation();
observed.Observe(ActivityEvent("task_started", observationNow.AddSeconds(-30)));
Check(observed.Item(taskMetadata, observationNow) is { State: "working", AgeS: 30 }, "recorded unfinished recent task is working");
Check(observed.Item(taskMetadata, observationNow.AddSeconds(91)).State == "unknown", "unfinished task without activity for120s becomes unknown");
observed.Observe(ActivityEvent("item_completed", observationNow.AddSeconds(-2)));
Check(observed.Item(taskMetadata, observationNow) is { State: "working", AgeS: 2 }, "new activity refreshes observed working age");
observed.Observe(ActivityEvent("task_complete", observationNow.AddSeconds(-1), "other-turn"));
Check(observed.Item(taskMetadata, observationNow).State == "working", "unrelated turn completion does not close current task");
observed.Observe(ActivityEvent("task_complete", observationNow));
Check(observed.Item(taskMetadata, observationNow).State == "completed", "matching persisted completion closes working state");
observed.Observe(ActivityEvent("turn_started", observationNow, "turn-b"));
observed.Observe(ActivityEvent("turn_aborted", observationNow, "turn-b"));
Check(observed.Item(taskMetadata, observationNow).State == "interrupted", "turn aliases and explicit abort produce interrupted state");
var recent = new ActivityObservation(); recent.Observe(ActivityEvent("token_count", observationNow));
Check(recent.Item(taskMetadata, observationNow).State == "recent" && !recent.HasBoundary && recent.Item(taskMetadata, observationNow.AddSeconds(121)).State == "unknown", "tail without lifecycle boundary is recent then unknown, never inferred working");
var malformedObservation = new ActivityObservation();
foreach (var bad in new[] { "{", "[]", "null", "{\"type\":\"response_item\",\"payload\":{\"type\":\"task_started\"}}" }) malformedObservation.Observe(Encoding.UTF8.GetBytes(bad));
malformedObservation.Observe(ActivityEvent("task_started", observationNow.AddDays(1)));
Check(malformedObservation.Item(taskMetadata, observationNow).State == "unknown", "malformed, non-event and implausible future records cannot manufacture activity");
var splitObservation = new ActivityObservation();
var activityLines = new ActivityLines();
var startLine = ActivityEvent("task_started", observationNow).Concat(new byte[] { 10 }).ToArray();
activityLines.Feed(startLine.AsSpan(0, 19), splitObservation);
Check(!splitObservation.HasBoundary, "partial JSONL record waits for newline");
activityLines.Feed(startLine.AsSpan(19), splitObservation);
Check(splitObservation.Item(taskMetadata, observationNow).State == "working", "split JSONL record reassembles into lifecycle event");
var boundedLines = new ActivityLines(true);
var boundedObservation = new ActivityObservation();
boundedLines.Feed(Encoding.UTF8.GetBytes("unread prefix\n"), boundedObservation);
boundedLines.Feed(new byte[ActivityContract.MaxTailBytes + 1], boundedObservation);
boundedLines.Feed(new byte[] { 10 }, boundedObservation);
boundedLines.Feed(startLine, boundedObservation);
Check(boundedObservation.Item(taskMetadata, observationNow).State == "working", "truncated leading and oversized records are skipped through newline before normal parsing resumes");
var maximumActivity = new CodexActivityStats(true, true, "partial", 31536000, 1000, 1000,
    Enumerable.Range(0, 3).Select(_ => new CodexActivityItem(new string('"', 63), new string('\\', 31), "interrupted", 31536000)).ToArray());
var maximumActivitySample = maximumSample with { Codex = maximumSample.Codex! with { Activity = maximumActivity } };
var maximumActivityPayload = JsonSerializer.SerializeToUtf8Bytes(maximumActivitySample, Protocol.Json);
Check(maximumActivityPayload.Length <= 4096, "maximum escaped activity plus music, quota and metrics fit4096 bytes (" + maximumActivityPayload.Length + " bytes)");
using var activityJson = JsonDocument.Parse(maximumActivityPayload);
var activityRoot = activityJson.RootElement.GetProperty("codex").GetProperty("activity");
Check(activityRoot.GetProperty("age_s").TryGetUInt32(out _) && activityRoot.GetProperty("working_count").TryGetUInt32(out _) && activityRoot.GetProperty("items").GetArrayLength() == 3 && activityRoot.GetProperty("items")[0].GetProperty("age_s").TryGetUInt32(out _), "activity contract uses integral bounded age/count fields and three rows");
var installedCompanion = Path.Combine(Path.GetTempPath(), "ClawPal", "app", "ClawPal.Companion.exe");
var devCompanion = Path.Combine(Path.GetTempPath(), "source", "bin", "ClawPal.Companion.exe");
Check(StartupContract.ShouldRedirect([], devCompanion, installedCompanion, true), "normal development launch redirects to canonical installed companion");
Check(StartupContract.ShouldRedirect([StartupContract.StartupArgument], devCompanion, installedCompanion, true), "startup launch from another copy redirects with startup origin");
Check(!StartupContract.ShouldRedirect([], installedCompanion.ToUpperInvariant(), installedCompanion, true), "canonical Windows path comparison prevents redirect loop");
Check(!StartupContract.ShouldRedirect([], devCompanion, installedCompanion, false), "development launch remains usable when no installed companion exists");
foreach (var ownCopyArgs in new[] { new[] { "--diagnostics", "result.json" }, new[] { "--activity-diagnostics", "result.json" }, new[] { "--test-connection", "result.json" }, new[] { "--set-startup", "--configure-only" }, new[] { "--pair-token-file", "private-path", "--configure-only" } })
    Check(!StartupContract.ShouldRedirect(ownCopyArgs, devCompanion, installedCompanion, true), "diagnostic and configuration launches operate on requested copy");
Check(StartupContract.ValidStartupLink(installedCompanion, "--startup-launch", installedCompanion, true), "startup link validates canonical target and startup argument");
Check(!StartupContract.ValidStartupLink(devCompanion, "--startup-launch", installedCompanion, true) && !StartupContract.ValidStartupLink(installedCompanion, "", installedCompanion, true) && !StartupContract.ValidStartupLink(installedCompanion, "--startup-launch", installedCompanion, false), "stale target, wrong arguments and missing installation cannot appear enabled");
Check(StartupContract.Origin([]) == "manual" && StartupContract.Origin(["--startup-launch"]) == "windows-startup" && StartupContract.Origin(["--diagnostics", "private-path"]) == "diagnostic" && StartupContract.Origin(["--pair-token-file", "private-path"]) == "configuration", "launch logging emits controlled origin categories without command argument values");
var profileRoot = Path.Combine(Path.GetTempPath(), "User Profiles", "Buddy Test");
var stableData = BuddyPaths.DataDirectory(profileRoot);
var stableExecutable = BuddyPaths.InstalledExecutable(profileRoot);
var unredirectedLocal = Path.Combine(profileRoot, "AppData", "Local");
var packagedLocal = Path.Combine(unredirectedLocal, "Packages", "OpenAI.Codex_test", "LocalCache", "Local");
Check(new[] { unredirectedLocal, packagedLocal }.All(local =>
    !stableData.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    && !stableExecutable.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    && Path.GetDirectoryName(stableData) == profileRoot,
    "shared profile data/install paths remain outside both Explorer and packaged AppData views");
Check(StartupContract.SamePath(BuddyPaths.InstalledExecutable(profileRoot + Path.DirectorySeparatorChar), stableExecutable)
    && StartupContract.SamePath(BuddyPaths.InstalledExecutable(Path.Combine(profileRoot, ".")), stableExecutable),
    "equivalent profile spelling preserves canonical installation identity and avoids redirect loops");
Check(Path.GetDirectoryName(stableExecutable) == BuddyPaths.InstalledDirectory(profileRoot)
    && Path.GetDirectoryName(BuddyPaths.InstalledDirectory(profileRoot)) == stableData,
    "configuration and executable share a single per-user root even when profile contains spaces");
bool RejectProfile(string candidate) { try { BuddyPaths.DataDirectory(candidate); return false; } catch (ArgumentException) { return true; } }
Check(RejectProfile("") && RejectProfile("relative-profile") && RejectProfile(Path.GetPathRoot(profileRoot)!),
    "missing, relative and filesystem-root profiles fail instead of choosing another data location");
Check(!GameContract.ShouldCapture("chrome.exe", false, true, 99, false) &&
      !GameContract.ShouldCapture("Blender", false, true, 99, false) &&
      !GameContract.ShouldCapture("Battle.net", true, true, 99, false), "browsers, launchers and known graphics tools do not become games at high GPU load");
Check(GameContract.ShouldCapture("game", true, false, 12, false) &&
      GameContract.ShouldCapture("game", false, true, 70, false) &&
      !GameContract.ShouldCapture("game", false, true, 69, false) &&
      !GameContract.ShouldCapture("game", false, false, 99, false), "known games work at low load and fallback requires fullscreen plus 70 percent GPU");
Check(GameContract.ShouldCapture("game", false, true, 40, true) &&
      !GameContract.ShouldCapture("game", false, true, double.NaN, false), "capture hysteresis avoids load flicker and unavailable GPU cannot trigger fallback");
Check(GameContract.IsKnownGamePath(@"D:\Steam\steamapps\common\Example\game.exe") &&
      !GameContract.IsKnownGamePath(@"C:\Windows\notepad.exe"), "game directories are recognized without inspecting unrelated files");
Check(PresentMonFrames.Csv("\"Game, Deluxe.exe\",42,\"a\"\"b\"").SequenceEqual(new[] { "Game, Deluxe.exe", "42", "a\"b" }), "PresentMon CSV supports quoted commas and escaped quotes");
var fpsFrames = new PresentMonFrames();
fpsFrames.Add("Application,ProcessID,SwapChainAddress,TimeInSeconds,msBetweenPresents", 42, 1000);
for (int i = 1; i <= 120; i++) fpsFrames.Add(FormattableString.Invariant($"game.exe,42,0x01,{i / 60d},{1000d / 60}"), 42, 1000);
Check(fpsFrames.Fps(1000) == 60, "FPS is measured from frame intervals");
for (int i = 1; i <= 60; i++) fpsFrames.Add(FormattableString.Invariant($"game.exe,42,0x02,{i / 30d},{1000d / 30}"), 42, 1000);
Check(fpsFrames.Fps(1000) == 60, "a second swap chain does not double-count FPS");
fpsFrames.Add("other.exe,43,0x03,2,1", 42, 1000);
fpsFrames.Add("game.exe,42,0x01,NaN,1", 42, 1000);
fpsFrames.Add("game.exe,42,0x01,2,0", 42, 1000);
Check(fpsFrames.Fps(1000) == 60 && fpsFrames.Fps(4001) is null, "wrong PID and invalid intervals are ignored and stale FPS expires");
var gamingPayload = JsonSerializer.SerializeToUtf8Bytes(maximumActivitySample with { Game = new(true, new string('"', 95), 2000, "unavailable"), AutoOpenApps = false }, Protocol.Json);
Check(gamingPayload.Length <= 4096, "maximum game, music, Codex and telemetry fit the device frame budget (" + gamingPayload.Length + " bytes)");
var bothProviders = JsonSerializer.SerializeToUtf8Bytes(maximumActivitySample with { Claude=maximumActivitySample.Codex,Game=new(true,new string('"',95),2000,"unavailable"),AutoOpenApps=false },Protocol.Json);
Check(bothProviders.Length<=6144,"both AI providers, escaped task titles, music, gaming and telemetry fit 6144 bytes ("+bothProviders.Length+" bytes)");
// Signed requests. Vectors were computed independently with Python's hmac module and are
// repeated in tests/firmware_host/test_auth.c, so the display and companion must agree.
const string challenge = "0123456789abcdef0123456789abcdef";
byte[] HelloV2(string proof, string c = challenge) => JsonSerializer.SerializeToUtf8Bytes(new { type = "buddy.hello", v = 2, id, name = "ClawPal", port = Protocol.MonitorPort, nonce, challenge = c, proof });
var proofV2 = Protocol.Proof(token, nonce, id, challenge);
Check(Protocol.Proof(token, "fedcba9876543210fedcba9876543210", id, challenge) == "d6c75188c12b1d1a23ef825dd6941504cab7c94a30df0dce5eaf47918743709a", "protocol 2 discovery proof matches the firmware vector");
Check(Protocol.ValidateHello(HelloV2(proofV2), address, token, nonce, id)?.Challenge == challenge, "protocol 2 hello yields a signing challenge");
Check(Protocol.ValidateHello(Hello(proof), address, token, nonce, id)?.Challenge is null, "protocol 1 hello keeps bearer authentication for older displays");
Check(Protocol.ValidateHello(HelloV2(proof), address, token, nonce, id) is null, "protocol 2 hello with a protocol 1 proof is rejected");
Check(Protocol.ValidateHello(HelloV2(proofV2, "fedcba9876543210fedcba9876543210"), address, token, nonce, id) is null, "swapped challenge is rejected");
Check(Protocol.ValidateHello(HelloV2(Protocol.Proof(token, nonce, id, "0123456789ABCDEF0123456789ABCDEF"), "0123456789ABCDEF0123456789ABCDEF"), address, token, nonce, id) is null, "noncanonical challenge is rejected");
Check(DeviceAuth.Signature(token, "GET", "/settings", challenge, 1, DeviceAuth.EmptyBodyHash) == "7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c", "GET signature matches the firmware vector");
Check(DeviceAuth.BodyHash(Encoding.UTF8.GetBytes("{\"action\":\"wifi\"}")) == "82e9ccc751483310ef92e697844b03dcdb459ea5ebeb57bbfd0264b7a3450482", "body hash matches the firmware vector");
Check(DeviceAuth.Signature(token, "POST", "/confirm", challenge, 7, "82e9ccc751483310ef92e697844b03dcdb459ea5ebeb57bbfd0264b7a3450482") == "d9da15113a88771cdffded553fb8a99c00f3136e092280bc26c062351d8dd04c", "POST signature matches the firmware vector");
var signer = new DeviceAuth(token, challenge);
var first = signer.Authorization("GET", "/settings", DeviceAuth.EmptyBodyHash);
Check(first == "ClawPal-HMAC c=" + challenge + ",n=1,s=7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c", "first signed request uses counter 1");
Check(signer.Authorization("GET", "/settings", DeviceAuth.EmptyBodyHash).Contains(",n=2,"), "every request gets a new counter");
Check(!first.Contains(token) && new DeviceAuth(token, null).Authorization("GET", "/settings", DeviceAuth.EmptyBodyHash) == "Bearer " + token, "the token is sent only to protocol 1 displays");
var seen = new List<HttpRequestMessage>();
using (var http = new DeviceAuth(token, challenge).CreateClient(TimeSpan.FromSeconds(5), new RecordingHandler(seen)))
{
    using var body = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"action\":\"wifi\"}"));
    await http.PostAsync("http://192.168.1.10:42171/confirm", body);
    await http.GetAsync("http://192.168.1.10:42171/settings");
    using var upload = new HttpRequestMessage(HttpMethod.Post, "http://192.168.1.10:42171/firmware") { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
    upload.Headers.Add("X-ClawPal-SHA256", new string('A', 64));
    await http.SendAsync(upload);
}
string Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : "";
Check(Header(seen[0], "Authorization").StartsWith("ClawPal-HMAC c=" + challenge + ",n=1,s=") && Header(seen[0], DeviceAuth.BodyHashHeader) == "82e9ccc751483310ef92e697844b03dcdb459ea5ebeb57bbfd0264b7a3450482", "client signs POST bodies with their hash");
Check(Header(seen[1], "Authorization") == "ClawPal-HMAC c=" + challenge + ",n=2,s=" + DeviceAuth.Signature(token, "GET", "/settings", challenge, 2, DeviceAuth.EmptyBodyHash) && Header(seen[1], DeviceAuth.BodyHashHeader) == DeviceAuth.EmptyBodyHash, "client signs GET with the empty body hash");
Check(Header(seen[2], DeviceAuth.BodyHashHeader) == new string('a', 64) && Header(seen[2], "Authorization").Contains(",n=3,"), "firmware upload is signed with its declared image hash");
var legacy = new List<HttpRequestMessage>();
using (var http = new DeviceAuth(token, null).CreateClient(TimeSpan.FromSeconds(5), new RecordingHandler(legacy))) await http.GetAsync("http://192.168.1.10:42171/settings");
Check(Header(legacy[0], "Authorization") == "Bearer " + token && Header(legacy[0], DeviceAuth.BodyHashHeader) == "", "older displays keep receiving the bearer header only");
if (args.Length == 2 && args[0] == "--presentmon-sample") {
    var lines = File.ReadAllLines(args[1]);
    var firstFrame = lines.Skip(1).Select(PresentMonFrames.Csv).First(v => v.Length > 2 && int.TryParse(v[1], out _));
    int realPid = int.Parse(firstFrame[1]);
    var recorded = new PresentMonFrames();
    foreach (var line in lines) recorded.Add(line, realPid, 1000);
    var realFps = recorded.Fps(1000);
    Check(realFps is > 0 and <= 2000, "real PresentMon capture parses into a measured frame rate (" + realFps + ")");
}
Console.WriteLine($"{passed} checks passed.");

sealed class RecordingHandler(List<HttpRequestMessage> seen) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { seen.Add(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
}
