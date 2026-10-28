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
    // Display app IDs that a "focus" control may name.
    public const int CodexApp = 4, ClaudeApp = 7;
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
                if (field.Name is not ("type" or "v" or "id" or "action" or "value") || !fields.Add(field.Name)) return null;
            bool valued = fields.Contains("value");
            if (fields.Count != (valued ? 5 : 4) || root.GetProperty("type").GetString() != "buddy.control" ||
                root.GetProperty("v").GetInt32() != 1 || !root.GetProperty("id").TryGetInt32(out var id) || id <= 0) return null;
            var action = root.GetProperty("action").GetString();
            // Seek (seconds), volume (percent) and focus (app ID) carry a value; toggles never do.
            if (valued != (action is "seek" or "volume" or "focus")) return null;
            int value = 0;
            if (valued && (!root.GetProperty("value").TryGetInt32(out value) || value < 0 || value > (action == "volume" ? 100 : 604800))) return null;
            if (action == "focus" && value is not (CodexApp or ClaudeApp)) return null;
            return action is "play_pause" or "next" or "previous" or "shuffle" or "repeat" or "mute" or "seek" or "volume" or "focus" ? new DeviceControl(id, action, value) : null;
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AutoOpenApps = null,
    // Locked or about to sleep; omitted otherwise. The display then naps instead of reporting offline.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Away = null);
// Repeat follows MediaPlaybackAutoRepeatMode: 0 off, 1 track, 2 list. ArtColor is
// 0xRRGGBB picked from the album cover, or 0 when the display should keep its accent.
// Source names the player; ArtId names the cover uploaded to POST /art, 0 when none.
public sealed record MusicStats(bool Running, bool Available, string Title, string Artist, string Album, bool Playing,
    long PositionS, long DurationS, bool CanPlayPause, bool CanNext, bool CanPrevious, int ControlId = 0, bool ControlOk = false, string ControlError = "",
    bool CanShuffle = false, bool Shuffle = false, bool CanRepeat = false, int Repeat = 0, bool CanSeek = false,
    bool CanVolume = false, int Volume = 0, bool Muted = false, int ArtColor = 0, string Source = "", int ArtId = 0);
public sealed record DeviceControl(int Id, string Action, int Value = 0);
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

    // Windows media sessions identify players by AppUserModelID; these are the common ones.
    private static readonly (string Id, string Name)[] KnownPlayers =
    [
        ("AppleInc.AppleMusicWin_", "Apple Music"), ("AppleInc.iTunes_", "iTunes"), ("Microsoft.ZuneMusic_", "Media Player"),
        ("Microsoft.ZuneVideo_", "Films & TV"), ("308046B0AF4A39CB", "Firefox"), ("MSEdge", "Edge"),
        ("chrome.exe", "Chrome"), ("msedge.exe", "Edge"), ("firefox.exe", "Firefox"), ("brave.exe", "Brave"), ("opera.exe", "Opera"),
        ("vivaldi.exe", "Vivaldi"), ("TIDAL.exe", "TIDAL"), ("Deezer.exe", "Deezer"), ("Amazon Music.exe", "Amazon Music"),
        ("iTunes.exe", "iTunes"), ("vlc.exe", "VLC"), ("foobar2000.exe", "foobar2000"), ("MusicBee.exe", "MusicBee"),
        ("AIMP.exe", "AIMP"), ("Winamp.exe", "Winamp"), ("YouTube Music.exe", "YouTube Music"),
    ];

    public static string SourceName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        if (IsSpotifySession(appId)) return "Spotify";
        string id = appId.Trim();
        foreach (var (known, player) in KnownPlayers)
            if (known.EndsWith(".exe") ? id.Equals(known, StringComparison.OrdinalIgnoreCase) || id.EndsWith("\\" + known, StringComparison.OrdinalIgnoreCase)
                : id.StartsWith(known, StringComparison.OrdinalIgnoreCase)) return player;
        // Win32 players report their executable; packaged ones "Publisher.Name_hash!Entry".
        string name = Path.GetFileName(id.Replace('/', '\\'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (name.IndexOf('_') is > 0 and var end) name = name[..end].Split('.').Last();
        return LimitText(name, 31);
    }

    // The volume mixer lists Win32 players by process; packaged and browser-tab identities are not mapped.
    public static string? ProcessName(string? appId)
    {
        if (IsSpotifySession(appId)) return "Spotify";
        if (string.IsNullOrWhiteSpace(appId) || !appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileNameWithoutExtension(appId.Replace('/', '\\'));
        return name.Length > 0 ? name : null;
    }

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

    // Picks the dominant vivid hue of a small BGRA cover thumbnail and lifts it into a
    // range that reads on the display's dark background. Grey covers return 0.
    public static int AccentColor(ReadOnlySpan<byte> bgra)
    {
        int pixels = bgra.Length / 4;
        if (pixels == 0) return 0;
        Span<double> bins = stackalloc double[12 * 4];
        double total = 0;
        for (int i = 0; i < pixels * 4; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0, alpha = bgra[i + 3] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
            // Vivid mid-tones count most; highlights, shadows and greys barely register.
            double weight = chroma * chroma * (1 - Math.Abs(max + min - 1)) * alpha;
            if (weight <= 0) continue;
            int bin = (int)(Hue(r, g, b, max, chroma) * 2) % 12;
            bins[bin * 4] += weight; bins[bin * 4 + 1] += r * weight; bins[bin * 4 + 2] += g * weight; bins[bin * 4 + 3] += b * weight;
            total += weight;
        }
        if (total < pixels * 0.004) return 0;
        int best = 0;
        for (int bin = 1; bin < 12; ++bin) if (bins[bin * 4] > bins[best * 4]) best = bin;
        double weightSum = bins[best * 4], red = bins[best * 4 + 1] / weightSum, green = bins[best * 4 + 2] / weightSum, blue = bins[best * 4 + 3] / weightSum;
        double high = Math.Max(red, Math.Max(green, blue)), low = Math.Min(red, Math.Min(green, blue)), lightness = (high + low) / 2;
        double saturation = high == low ? 0 : (high - low) / (1 - Math.Abs(2 * lightness - 1));
        double hue = Hue(red, green, blue, high, high - low);
        return Rgb(hue, Math.Clamp(saturation, 0.5, 0.85), Math.Clamp(lightness, 0.58, 0.7));
    }

    // BGRA8 to RGB565 little-endian, the display's native pixel format.
    public static byte[] Rgb565(ReadOnlySpan<byte> bgra)
    {
        var result = new byte[bgra.Length / 4 * 2];
        for (int i = 0, o = 0; i + 3 < bgra.Length; i += 4, o += 2)
        {
            int value = (bgra[i + 2] >> 3) << 11 | (bgra[i + 1] >> 2) << 5 | bgra[i] >> 3;
            result[o] = (byte)value;
            result[o + 1] = (byte)(value >> 8);
        }
        return result;
    }

    // A stable positive ID for a cover's pixels, so the display only redraws a new cover.
    public static int CoverId(ReadOnlySpan<byte> pixels)
    {
        int id = BitConverter.ToInt32(SHA256.HashData(pixels)) & int.MaxValue;
        return id == 0 ? 1 : id;
    }

    private static double Hue(double r, double g, double b, double max, double chroma)
    {
        if (chroma <= 0) return 0;
        double hue = max == r ? (g - b) / chroma : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        return hue < 0 ? hue + 6 : hue;
    }

    private static int Rgb(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation, x = chroma * (1 - Math.Abs(hue % 2 - 1)), m = lightness - chroma / 2;
        (double r, double g, double b) = (int)hue switch { 0 => (chroma, x, 0.0), 1 => (x, chroma, 0.0), 2 => (0.0, chroma, x), 3 => (0.0, x, chroma), 4 => (x, 0.0, chroma), _ => (chroma, 0.0, x) };
        int Channel(double value) => (int)Math.Round(Math.Clamp(value + m, 0, 1) * 255);
        return Channel(r) << 16 | Channel(g) << 8 | Channel(b);
    }
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
