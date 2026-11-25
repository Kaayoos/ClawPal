using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawPal.Companion;

// One LRCLIB record, as much of it as ClawPal uses.
public sealed record LrclibRecord(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("artistName")] string? ArtistName,
    [property: JsonPropertyName("albumName")] string? AlbumName,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("instrumental")] bool Instrumental,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);

public enum LrclibOutcome { Found, NotFound, Failed }

// LRCLIB (https://lrclib.net) is a free, open lyrics database run by its community. It asks
// clients to name themselves in the User-Agent and to honour Retry-After; requests go one at a
// time. Only the song's title, artist, album and length are sent, and only while synced lyrics
// are switched on in the PC app.
public sealed class LrclibClient
{
    public static readonly Uri Base = new("https://lrclib.net/");
    private readonly HttpClient http;
    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;

    public static string UserAgent =>
        $"ClawPal/{typeof(LrclibClient).Assembly.GetName().Version?.ToString(3) ?? "0"} (https://github.com/Kaayoos/clawpal)";

    public LrclibClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // GET /api/get first: an exact match on title, artist, album and length (within 2 s).
    // Then /api/search, keeping only a result whose length fits, so a cover version or a
    // different edit never borrows another song's timing. "Fits" allows for Spotify's crossfade
    // (up to 12 s), which can make a player report a song that much shorter than it is.
    public async Task<(LrclibOutcome Outcome, LrclibRecord? Record)> FindAsync(string title, string artist, string album, long durationS, CancellationToken cancellationToken)
    {
        var query = new List<string> { "track_name=" + Uri.EscapeDataString(title), "artist_name=" + Uri.EscapeDataString(artist) };
        if (album.Length > 0) query.Add("album_name=" + Uri.EscapeDataString(album));
        if (durationS is >= 1 and <= 3600) query.Add("duration=" + durationS.ToString(CultureInfo.InvariantCulture));
        var (status, body) = await GetAsync("api/get?" + string.Join('&', query), cancellationToken);
        if (status == HttpStatusCode.OK && body is not null)
        {
            var record = JsonSerializer.Deserialize<LrclibRecord>(body);
            if (record is not null) return (LrclibOutcome.Found, record);
        }
        if (status != HttpStatusCode.NotFound) return (LrclibOutcome.Failed, null);
        (status, body) = await GetAsync($"api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", cancellationToken);
        if (status != HttpStatusCode.OK || body is null) return (status == HttpStatusCode.NotFound ? LrclibOutcome.NotFound : LrclibOutcome.Failed, null);
        var results = JsonSerializer.Deserialize<List<LrclibRecord>>(body) ?? [];
        var best = PickSearchResult(results, artist, durationS);
        return best is null ? (LrclibOutcome.NotFound, null) : (LrclibOutcome.Found, best);
    }

    public const int CrossfadeS = 13;
    public static bool LengthFits(double? lyricsS, long reportedS) =>
        reportedS <= 0 || (lyricsS is { } d && d - reportedS >= -3 && d - reportedS <= CrossfadeS);

    // The same length first, then synced lyrics, the same artist, and the nearest length.
    public static LrclibRecord? PickSearchResult(IEnumerable<LrclibRecord> results, string artist, long durationS) =>
        results.Where(r => LengthFits(r.Duration, durationS))
            .OrderByDescending(r => durationS <= 0 || Math.Abs(r.Duration!.Value - durationS) <= 3)
            .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            .ThenByDescending(r => string.Equals(r.ArtistName?.Trim(), artist.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => durationS <= 0 ? 0 : Math.Abs(r.Duration!.Value - durationS))
            .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.PlainLyrics) || r.Instrumental)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics) || !string.IsNullOrWhiteSpace(r.PlainLyrics) || r.Instrumental);

    private async Task<(HttpStatusCode? Status, string? Body)> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < blockedUntil) return (null, null);
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                // Their rule: wait as long as Retry-After says, plus up to 30% jitter.
                var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(60));
                if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                blockedUntil = DateTimeOffset.UtcNow + wait * (1 + Random.Shared.NextDouble() * 0.3);
                Log.Write($"LRCLIB asked ClawPal to wait {wait.TotalSeconds:0} s.");
                return (response.StatusCode, null);
            }
            if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
            if (response.Content.Headers.ContentLength > 2_000_000) return (null, null);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Write("LRCLIB request failed: " + error.GetType().Name);
            return (null, null);
        }
    }
}

// Lyrics already fetched, one small file per song under %USERPROFILE%\.clawpal\lyrics, so a song
// played again is never fetched again. A song LRCLIB didn't have is remembered for a week, since
// LRCLIB's own crawler may add it later.
public sealed class LyricsCache(string directory)
{
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(7);
    public const int MaxEntries = 3000;
    public sealed record Entry(string Key, DateTimeOffset Fetched, bool Found, bool Instrumental, string? Synced, string? Plain);

    public static LyricsCache Default => new(Path.Combine(Configuration.DirectoryPath, "lyrics"));
    public string Directory => directory;
    private string PathFor(string key) => System.IO.Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".json");

    // Null when the song isn't cached, or was a miss long enough ago to ask again.
    public Entry? Get(string key, DateTimeOffset now)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            if (entry is null || entry.Key != key) return null;
            if (!entry.Found && now - entry.Fetched > MissLifetime) return null;
            return entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public void Put(Entry entry)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = PathFor(entry.Key);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
            File.Move(temporary, path, true);
            Prune();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Write("Lyrics cache write failed: " + error.GetType().Name); }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(directory).GetFiles("*.json");
        if (files.Length <= MaxEntries) return;
        foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxEntries + MaxEntries / 10))
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// Finds the lyrics of whatever Windows says is playing, one song at a time, and keeps the
// result ready for the display: the document to upload and the state to show.
public sealed class LyricsService
{
    public sealed record Result(string TrackKey, LyricsState State, LyricsDocument? Document);
    public Func<bool> Enabled { get; init; } = () => false;
    public Func<string, bool> Covers { get; init; } = DisplayGlyphs.Covers;
    private readonly LrclibClient client;
    private readonly LyricsCache cache;
    private Result current = new("", LyricsState.Loading, null);
    public event Action? Changed;

    public LyricsService(LrclibClient? client = null, LyricsCache? cache = null) { this.client = client ?? new LrclibClient(); this.cache = cache ?? LyricsCache.Default; }

    // The lyrics for this track, or Loading while they are still being looked up.
    public Result For(string trackKey)
    {
        var value = Volatile.Read(ref current);
        return value.TrackKey == trackKey ? value : new(trackKey, LyricsState.Loading, null);
    }

    public async Task RunAsync(MediaSessions media, CancellationToken cancellationToken)
    {
        string failedKey = ""; int failures = 0; long retryAt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(400, cancellationToken);
            if (!Enabled() || media.Playback is not { } playback || playback.Title.Length == 0 || playback.Artist.Length == 0) continue;
            string key = playback.TrackKey;
            var shown = Volatile.Read(ref current);
            // A failed lookup (offline, LRCLIB busy) is tried again after a minute, then two, then left.
            bool retry = shown.TrackKey == key && shown.State == LyricsState.Unavailable && key == failedKey &&
                failures < 3 && Environment.TickCount64 >= retryAt;
            if (shown.TrackKey == key && !retry) continue;
            if (!retry) Publish(new(key, LyricsState.Loading, null));
            var result = await ResolveAsync(playback, key, cancellationToken);
            if (result.State == LyricsState.Unavailable)
            {
                failures = key == failedKey ? failures + 1 : 1;
                failedKey = key; retryAt = Environment.TickCount64 + 60_000L * failures;
            }
            Publish(result);
        }
    }

    private void Publish(Result result)
    {
        var previous = Volatile.Read(ref current);
        Volatile.Write(ref current, result);
        if (previous.TrackKey != result.TrackKey || previous.State != result.State || previous.Document?.Id != result.Document?.Id) Changed?.Invoke();
    }

    public async Task<Result> ResolveAsync(PlaybackReport playback, string key, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = cache.Get(key, now);
        if (entry is null)
        {
            long durationS = (playback.DurationMs + 500) / 1000;
            LrclibOutcome outcome; LrclibRecord? record;
            try { (outcome, record) = await client.FindAsync(playback.Title, playback.Artist, playback.Album, durationS, cancellationToken); }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                Log.Write("LRCLIB answer unreadable: " + error.GetType().Name);
                return new(key, LyricsState.Unavailable, null);
            }
            if (outcome == LrclibOutcome.Failed) return new(key, LyricsState.Unavailable, null);
            entry = record is null ? new LyricsCache.Entry(key, now, false, false, null, null)
                : new LyricsCache.Entry(key, now, true, record.Instrumental, record.SyncedLyrics, record.PlainLyrics);
            cache.Put(entry);
        }
        return Build(key, entry, Covers);
    }

    public static Result Build(string key, LyricsCache.Entry entry, Func<string, bool> covers)
    {
        if (!entry.Found) return new(key, LyricsState.None, null);
        if (entry.Instrumental) return new(key, LyricsState.Instrumental, null);
        var synced = LyricsContract.ParseLrc(entry.Synced);
        if (synced.Any(l => l.Text?.Length > 0))
        {
            var (document, state) = LyricsContract.ForDisplay(key, LyricsKind.Synced, synced, covers);
            return new(key, state, document);
        }
        var plain = LyricsContract.ParsePlain(entry.Plain);
        var (plainDocument, plainState) = LyricsContract.ForDisplay(key, LyricsKind.Plain, plain, covers);
        return new(key, plainState, plainDocument);
    }
}

// The PC's monotonic clock in milliseconds, for reference points and clock sync. Not
// Environment.TickCount64, which moves in 16 ms steps.
public static class MonotonicClock
{
    public static long NowMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
}
