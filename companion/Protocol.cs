using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace ClawPal.Companion;

public static class Protocol
{
    public const int DiscoveryPort = 42170;
    public const int MonitorPort = 42171;
    // This JSON is carried as WebSocket data, never embedded in HTML. Keep Unicode
    // as UTF-8 so bounded media strings remain compact for the 4096-byte device limit.
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static byte[] DiscoveryRequest(string nonce) => Encoding.ASCII.GetBytes("BUDDYOS_DISCOVER_V1 " + nonce);

    public static string Proof(string token, string nonce, string id) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes($"{nonce}|{id}|{MonitorPort}"))).ToLowerInvariant();
    // Protocol 2 hellos also carry a fresh challenge for signed requests; the proof covers it.
    public static string Proof(string token, string nonce, string id, string challenge) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes($"{nonce}|{id}|{MonitorPort}|{challenge}"))).ToLowerInvariant();

    public static DeviceEndpoint? ValidateHello(ReadOnlySpan<byte> payload, IPAddress sender, string token, string nonce, string expectedId)
    {
        if (payload.Length > 1024 || !IsLanAddress(sender)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString();
            var proof = root.GetProperty("proof").GetString();
            int version = root.GetProperty("v").GetInt32();
            string? challenge = version == 2 ? root.GetProperty("challenge").GetString() : null;
            if (root.GetProperty("type").GetString() != "buddy.hello" || version is not (1 or 2) || (version == 2 && !IsLowerHex(challenge, 32)) ||
                root.GetProperty("port").GetInt32() != MonitorPort || root.GetProperty("nonce").GetString() != nonce ||
                !IsLowerHex(id, 12) || (!string.IsNullOrEmpty(expectedId) && id != expectedId) || !IsLowerHex(proof, 64)) return null;
            var supplied = Convert.FromHexString(proof!);
            var actual = Convert.FromHexString(challenge is null ? Proof(token, nonce, id!) : Proof(token, nonce, id!, challenge));
            return CryptographicOperations.FixedTimeEquals(supplied, actual) ? new DeviceEndpoint(sender, id!, challenge) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return null; }
    }

    public static bool IsLowerHex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static long? ParseAcknowledgement(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "buddy.ack" || root.GetProperty("v").GetInt32() != 1 ||
                !root.GetProperty("seq").TryGetInt64(out var sequence) || sequence < 0) return null;
            return sequence;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return null; }
    }
    public static DeviceControl? ParseControl(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 512) return null;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (field.Name is not ("type" or "v" or "id" or "action") || !fields.Add(field.Name)) return null;
            if (fields.Count != 4 || root.GetProperty("type").GetString() != "buddy.control" ||
                root.GetProperty("v").GetInt32() != 1 || !root.GetProperty("id").TryGetInt32(out var id) || id <= 0) return null;
            var action = root.GetProperty("action").GetString();
            return action is "play_pause" or "next" or "previous" ? new DeviceControl(id, action) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return null; }
    }
    public static bool IsLanAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }
}

public sealed class Acknowledgements
{
    private readonly object gate = new();
    private readonly HashSet<long> sent = new();
    private long lastAcknowledged = -1;
    public void RecordSent(long sequence) { lock (gate) sent.Add(sequence); }
    public bool Accept(ReadOnlySpan<byte> payload)
    {
        var parsed = Protocol.ParseAcknowledgement(payload);
        if (parsed is not { } sequence) return false;
        lock (gate)
        {
            if (sequence <= lastAcknowledged || !sent.Contains(sequence)) return false;
            lastAcknowledged = sequence;
            sent.RemoveWhere(value => value <= sequence);
            return true;
        }
    }
}

public sealed class TextMessageBuffer
{
    private readonly MemoryStream contents = new();
    public byte[]? AddFrame(ReadOnlySpan<byte> payload, WebSocketMessageType type, bool endOfMessage)
    {
        if (type == WebSocketMessageType.Close) throw new IOException("Buddy closed its connection before acknowledging telemetry.");
        if (type != WebSocketMessageType.Text) throw new IOException("Buddy sent a non-text acknowledgement.");
        if (contents.Length + payload.Length > 4096) throw new IOException("Buddy sent an oversized acknowledgement.");
        contents.Write(payload);
        if (!endOfMessage) return null;
        var message = contents.ToArray();
        contents.SetLength(0);
        return message;
    }
}

// Challenge is set for displays that accept signed requests (see DeviceAuth).
public sealed record DeviceEndpoint(IPAddress Address, string Id, string? Challenge = null);
public sealed record CpuStats(double? Usage, double? Temperature, string Name);
public sealed record GpuStats(double? Usage, double? Temperature, double? VramUsedMb, double? VramTotalMb, string Name);
public sealed record MemoryStats(double UsedMb, double TotalMb, double Usage);
public sealed record NetworkStats(double RxBps, double TxBps);
public sealed record PcStats(string Type, int V, long Seq, string Hostname, long Timestamp, CpuStats Cpu, GpuStats Gpu, MemoryStats Memory, NetworkStats Network, long UptimeS,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MusicStats? Music = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CodexStats? Codex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CodexStats? Claude = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameStats? Game = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AutoOpenApps = null);
public sealed record MusicStats(bool Running, bool Available, string Title, string Artist, string Album, bool Playing,
    long PositionS, long DurationS, bool CanPlayPause, bool CanNext, bool CanPrevious, int ControlId = 0, bool ControlOk = false, string ControlError = "");
public sealed record DeviceControl(int Id, string Action);
public sealed record MusicControlResult(int Id, bool Ok, string Error);

public sealed class ControlSequence
{
    private int lastId;
    public bool Accept(DeviceControl command)
    {
        if (command.Id <= lastId) return false;
        lastId = command.Id;
        return true;
    }
}

public static class MusicContract
{
    public static bool IsSpotifySession(string? appId) =>
        string.Equals(appId, "Spotify.exe", StringComparison.OrdinalIgnoreCase) ||
        (appId?.StartsWith("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!", StringComparison.OrdinalIgnoreCase) == true && appId.Length > "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!".Length);

    public static string LimitText(string? text, int maxUtf8Bytes)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = new StringBuilder();
        int bytes = 0;
        foreach (var raw in text.EnumerateRunes())
        {
            var rune = Rune.IsControl(raw) || raw.Value is 0x2028 or 0x2029 ? new Rune(' ') : raw;
            if (bytes + rune.Utf8SequenceLength > maxUtf8Bytes) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString().Trim();
    }

    public static long Seconds(double value) => double.IsFinite(value) ? (long)Math.Clamp(Math.Floor(value), 0, 604800) : 0;
}

public static class CounterMath
{
    public static double? CpuUsage(ulong previousIdle, ulong previousKernel, ulong previousUser, ulong idle, ulong kernel, ulong user)
    {
        if (idle < previousIdle || kernel < previousKernel || user < previousUser) return null;
        var total = (double)(kernel - previousKernel) + (user - previousUser);
        return total <= 0 ? null : Math.Round(Math.Clamp((total - (idle - previousIdle)) / total * 100, 0, 100), 1);
    }

    public static double BytesPerSecond(long previous, long current, double elapsed) =>
        current < previous || previous < 0 || elapsed <= 0 || !double.IsFinite(elapsed) ? 0 : Math.Round((current - previous) / elapsed, 1);
}
