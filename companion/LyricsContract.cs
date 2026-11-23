using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClawPal.Companion;

// One line of lyrics. TimeMs is when it is sung (0 for every line of plain lyrics). Text is
// null when the display's fonts can't draw the line; the display then shows a music note
// rather than empty boxes.
public sealed record LyricLine(int TimeMs, string? Text);

public enum LyricsKind { Synced, Plain }

// The lyrics of one track as the display receives them (POST /lyrics).
public sealed record LyricsDocument(int Id, LyricsKind Kind, IReadOnlyList<LyricLine> Lines);

// What the display should say about the current track's lyrics. Wire names in Wire().
public enum LyricsState { Loading, Synced, Plain, None, Instrumental, Unsupported, Unavailable }

// The pure parts of synced lyrics: parsing LRCLIB's answer, fitting it to the display, and the
// timing maths that keeps the display's lines in step with the PC's player over Wi-Fi.
public static class LyricsContract
{
    public const int MaxLines = 400, MaxLineBytes = 255, MaxBodyBytes = 49152;
    // The longest position a reference point may carry, matching the display's seek limit.
    public const long MaxPositionMs = 604_800_000;
    public const int MaxOffsetMs = 1000;

    public static string Wire(LyricsState state) => state switch
    {
        LyricsState.Loading => "loading", LyricsState.Synced => "synced", LyricsState.Plain => "plain", LyricsState.None => "none",
        LyricsState.Instrumental => "instrumental", LyricsState.Unsupported => "unsupported", _ => "unavailable",
    };

    // [mm:ss], [mm:ss.x], [mm:ss.xx], [mm:ss.xxx] and [mm:ss:xx]; a line may carry several.
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant);
    // Enhanced LRC marks words with <mm:ss.xx>; this display shows whole lines, so they go.
    private static readonly Regex WordTag = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant);
    private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d{1,6})\s*\]$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Parses LRC into lines ordered by time. Metadata tags are skipped; an [offset:] tag moves
    // every line (positive means earlier, as LRC defines it). Lines with no time tag are dropped.
    public static List<LyricLine> ParseLrc(string? lrc)
    {
        var lines = new List<(int Time, int Order, string Text)>();
        if (string.IsNullOrWhiteSpace(lrc)) return [];
        int offset = 0, order = 0;
        foreach (var raw in lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (OffsetTag.Match(line) is { Success: true } shift) { offset = int.Parse(shift.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            // Leading time tags, possibly separated by spaces; metadata tags like [ar:] have none.
            var times = new List<int>();
            int at = 0;
            while (true)
            {
                int start = at;
                while (start < line.Length && line[start] == ' ') start++;
                var tag = TimeTag.Match(line, start);
                if (!tag.Success || tag.Index != start) break;
                int minutes = int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture), seconds = int.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture);
                string fraction = tag.Groups[3].Value;
                int ms = fraction.Length == 0 ? 0 : int.Parse(fraction, CultureInfo.InvariantCulture) * (fraction.Length == 1 ? 100 : fraction.Length == 2 ? 10 : 1);
                if (seconds < 60) times.Add(minutes * 60_000 + seconds * 1000 + ms);
                at = tag.Index + tag.Length;
            }
            if (times.Count == 0) continue;
            string text = CleanText(WordTag.Replace(line[at..], ""));
            foreach (int time in times) lines.Add((Math.Max(0, time - offset), order++, text));
        }
        return lines.OrderBy(l => l.Time).ThenBy(l => l.Order).Select(l => new LyricLine(l.Time, l.Text)).ToList();
    }

    // Plain lyrics: one entry per line, blank lines kept as verse breaks but never doubled.
    public static List<LyricLine> ParsePlain(string? plain)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(plain)) return result;
        foreach (var raw in plain.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string text = CleanText(raw);
            if (text.Length == 0 && (result.Count == 0 || result[^1].Text == "")) continue;
            result.Add(new LyricLine(0, text));
        }
        while (result.Count > 0 && result[^1].Text == "") result.RemoveAt(result.Count - 1);
        return result;
    }

    // Composed, single-spaced text without control characters.
    public static string CleanText(string text)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var r = Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029 || Rune.IsWhiteSpace(rune) ? new Rune(' ') : rune;
            if (r.Value == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
            builder.Append(r.ToString());
        }
        return builder.ToString().Trim();
    }

    // Fits lyrics to the display: bounded line count and length, and lines its fonts can't draw
    // replaced with null. Returns Unsupported when most of the song couldn't be shown at all.
    public static (LyricsDocument? Document, LyricsState State) ForDisplay(string trackKey, LyricsKind kind, IReadOnlyList<LyricLine> lines, Func<string, bool> covers)
    {
        if (lines.Count == 0) return (null, LyricsState.None);
        var fitted = new List<LyricLine>();
        int words = 0, missing = 0;
        foreach (var line in lines.Take(MaxLines))
        {
            string text = MusicContract.LimitText(line.Text, MaxLineBytes);
            if (text.Length == 0) { fitted.Add(line with { Text = "" }); continue; }
            ++words;
            if (covers(text)) fitted.Add(line with { Text = text });
            else { ++missing; fitted.Add(line with { Text = null }); }
        }
        if (words == 0) return (null, LyricsState.None);
        if (missing * 2 > words) return (null, LyricsState.Unsupported);
        var document = new LyricsDocument(DocumentId(trackKey, kind, fitted), kind, fitted);
        while (DisplayJson(document).Length > MaxBodyBytes && fitted.Count > 1)
        {
            fitted.RemoveAt(fitted.Count - 1);
            document = document with { Lines = fitted.ToList() };
        }
        return (document, kind == LyricsKind.Synced ? LyricsState.Synced : LyricsState.Plain);
    }

    // {"id":7,"kind":"synced","lines":[[12340,"First line"],[15000,null],...]}
    public static byte[] DisplayJson(LyricsDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", document.Id);
            writer.WriteString("kind", document.Kind == LyricsKind.Synced ? "synced" : "plain");
            writer.WriteStartArray("lines");
            foreach (var line in document.Lines)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(document.Kind == LyricsKind.Synced ? line.TimeMs : 0);
                if (line.Text is null) writer.WriteNullValue(); else writer.WriteStringValue(line.Text);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    // A positive ID for one track's lyrics, so the display keeps the right document.
    public static int DocumentId(string trackKey, LyricsKind kind, IEnumerable<LyricLine> lines)
    {
        var text = new StringBuilder(trackKey).Append('\n').Append(kind);
        foreach (var line in lines) text.Append('\n').Append(line.TimeMs).Append('\t').Append(line.Text ?? "\u0000");
        int id = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))) & int.MaxValue;
        return id == 0 ? 1 : id;
    }

    // One song as LRCLIB and the cache know it. Whole seconds: players round differently.
    public static string TrackKey(string title, string artist, string album, long durationS) =>
        string.Join('\u001f', CleanText(title).ToLowerInvariant(), CleanText(artist).ToLowerInvariant(), CleanText(album).ToLowerInvariant(),
            durationS.ToString(CultureInfo.InvariantCulture));

    // Where the playback is now, carried forward from the player's last report. Windows media
    // controls report the position only now and then (Spotify: on play, pause and seek), each
    // with the moment it was true.
    public static long PositionAt(long reportedMs, DateTimeOffset reportedAt, bool playing, double rate, DateTimeOffset now, long durationMs)
    {
        double position = reportedMs;
        if (playing)
        {
            double elapsed = Math.Max(0, (now - reportedAt).TotalMilliseconds);
            if (!double.IsFinite(rate) || rate <= 0) rate = 1;
            position += elapsed * rate;
        }
        long result = (long)Math.Round(Math.Max(0, position));
        if (durationMs > 0 && result > durationMs) result = durationMs;
        return Math.Min(result, MaxPositionMs);
    }

    // The display's clock seen from the PC, from one GET /clock: the PC's monotonic time when
    // the request left and when the answer came back, and the display's time in between.
    public readonly record struct ClockSample(long SentMs, uint DisplayMs, long ReceivedMs)
    {
        public long RoundTripMs => ReceivedMs - SentMs;
        // NTP's estimate: the display read its clock halfway through the round trip.
        public long OffsetMs => DisplayMs - (SentMs + ReceivedMs) / 2;
    }

    // Picks the sample with the shortest round trip; its midpoint guess is the tightest.
    // Returns null when no sample is usable (all too slow to trust).
    public static ClockSample? BestSample(IEnumerable<ClockSample> samples, long maxRoundTripMs = 400) =>
        samples.Where(s => s.RoundTripMs >= 0 && s.RoundTripMs <= maxRoundTripMs).OrderBy(s => s.RoundTripMs).Cast<ClockSample?>().FirstOrDefault();

    // A PC time in the display's clock, which counts milliseconds since it started and wraps at 2^32.
    public static uint DisplayTime(long pcMs, long offsetMs) => unchecked((uint)(pcMs + offsetMs));
}

// Decides when the display needs a new reference point. Between them the display counts on
// its own clock, so the PC sends one only when the player jumps, pauses, resumes, changes track,
// or every ten seconds as a safety resync.
public sealed class LyricsAnchors
{
    public const long ResyncMs = 10_000, JumpMs = 300;
    private string track = "";
    private bool playing, started;
    private long positionMs, atMs;
    public int Counter { get; private set; }
    // The next sample becomes a reference point whatever it says: the offset or clock changed.
    public void Invalidate() => started = false;
    public long PositionMs => positionMs;
    public long AtMs => atMs;
    public bool Playing => playing;

    // nowMs is the PC's monotonic time and position where the player is at that moment.
    // Returns true when this sample became the new reference point.
    public bool Update(string trackKey, bool isPlaying, long position, long nowMs)
    {
        long predicted = positionMs + (playing ? nowMs - atMs : 0);
        bool renew = !started || trackKey != track || isPlaying != playing || Math.Abs(predicted - position) > JumpMs || nowMs - atMs >= ResyncMs;
        if (!renew) return false;
        started = true; track = trackKey; playing = isPlaying; positionMs = position; atMs = nowMs;
        Counter = Counter == int.MaxValue ? 1 : Counter + 1;
        return true;
    }
}
