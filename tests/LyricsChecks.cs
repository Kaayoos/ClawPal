using ClawPal.Companion;
using System.Net;
using System.Text;
using System.Text.Json;

// Synced lyrics: LRC parsing, fitting lyrics to the display, carrying the player's position
// forward, measuring the display's clock, reference points, the cache and the LRCLIB client.
internal static class LyricsChecks
{
    internal static int Run(string directory)
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }

        // LRC parsing
        var lrc = LyricsContract.ParseLrc("[ar:Neon Harbor]\n[ti:Golden Hour Drive]\n[00:12.34]Headlights on the road\r\n[00:05.1][00:30.123] Twice  sung\n[01:02]  \n[00:20.50]<00:20.50>Word <00:21.00>by word\nno tag here\n");
        Check(lrc.Count == 5, "LRC: metadata and untimed lines are skipped, every time tag gives a line");
        Check(lrc.Select(l => l.TimeMs).SequenceEqual([5100, 12340, 20500, 30123, 62000]), "LRC: one, two and three digit fractions; lines come in time order");
        Check(lrc[0].Text == "Twice sung" && lrc[3].Text == "Twice sung", "LRC: a line with two times is sung twice, spaces tidied");
        Check(lrc[2].Text == "Word by word", "LRC: enhanced word tags are removed");
        Check(lrc[4].Text == "", "LRC: an empty timed line is kept as a pause");
        var shifted = LyricsContract.ParseLrc("[offset:+500]\n[00:10.00]Early\n[00:00.20]First");
        Check(shifted[0].TimeMs == 0 && shifted[1].TimeMs == 9500, "LRC: a positive offset tag moves lines earlier, never below zero");
        Check(LyricsContract.ParseLrc("[00:61.00]Bad seconds\n[00:01.00]Good").Count == 1, "LRC: impossible times are dropped");
        Check(LyricsContract.ParseLrc(null).Count == 0 && LyricsContract.ParseLrc("  ").Count == 0, "LRC: nothing parses to nothing");
        Check(LyricsContract.ParseLrc("[00:01.00]Café").Single().Text == "Café", "LRC: text is composed (NFC) so it matches the display's glyphs");

        var plain = LyricsContract.ParsePlain("\n\nVerse one\nline two\n\n\n\nVerse two\n\n");
        Check(plain.Select(l => l.Text).SequenceEqual(["Verse one", "line two", "", "Verse two"]) && plain.All(l => l.TimeMs == 0),
            "Plain lyrics: verse breaks kept once, leading and trailing blanks dropped");

        // Fitting lyrics to the display
        bool Latin(string text) => text.All(c => c < 0x180);
        var mixed = new List<LyricLine> { new(1000, "Hello"), new(2000, ""), new(3000, "こんにちは"), new(4000, "World") };
        var (document, state) = LyricsContract.ForDisplay("key", LyricsKind.Synced, mixed, Latin);
        Check(state == LyricsState.Synced && document!.Lines[2].Text is null && document.Lines[1].Text == "" && document.Lines[3].Text == "World",
            "Display fit: a line the fonts can't draw becomes null, a pause stays empty");
        Check(LyricsContract.ForDisplay("key", LyricsKind.Synced, [new(1, "日本語"), new(2, "テスト"), new(3, "ok")], Latin).State == LyricsState.Unsupported,
            "Display fit: a song mostly in letters the display lacks is Unsupported");
        Check(LyricsContract.ForDisplay("key", LyricsKind.Synced, [new(1, ""), new(2, "")], Latin).State == LyricsState.None &&
              LyricsContract.ForDisplay("key", LyricsKind.Plain, [], Latin).State == LyricsState.None, "Display fit: no words at all is None");
        var many = Enumerable.Range(0, 900).Select(i => new LyricLine(i * 1000, new string('a', 300))).ToList();
        var (bounded, _) = LyricsContract.ForDisplay("key", LyricsKind.Synced, many, Latin);
        Check(bounded!.Lines.Count <= LyricsContract.MaxLines && LyricsContract.DisplayJson(bounded).Length <= LyricsContract.MaxBodyBytes &&
              bounded.Lines.All(l => Encoding.UTF8.GetByteCount(l.Text!) <= LyricsContract.MaxLineBytes), "Display fit: lines, line length and body size stay within the display's limits");
        var json = JsonDocument.Parse(LyricsContract.DisplayJson(document!)).RootElement;
        Check(json.GetProperty("id").GetInt32() == document!.Id && json.GetProperty("kind").GetString() == "synced" &&
              json.GetProperty("lines")[0][0].GetInt32() == 1000 && json.GetProperty("lines")[0][1].GetString() == "Hello" &&
              json.GetProperty("lines")[2][1].ValueKind == JsonValueKind.Null, "POST /lyrics body: id, kind and [time, text|null] pairs");
        Check(document.Id > 0 && document.Id != LyricsContract.ForDisplay("other", LyricsKind.Synced, mixed, Latin).Document!.Id,
            "Document IDs are positive and differ between songs");
        Check(LyricsContract.TrackKey(" Song ", "Artist", "Album") == LyricsContract.TrackKey("song", "ARTIST", "album") &&
              LyricsContract.TrackKey("song", "artist", "album") != LyricsContract.TrackKey("song", "artist", "other album"), "Track keys ignore case and spacing");
        // Spotify with a 6 s crossfade reported "Całe lata" (237 s) now as 237, now as 231.
        Check(new PlaybackReport("Całe lata", "Taco Hemingway", "1-800-OŚWIECENIE", 237_218, 0, DateTimeOffset.UnixEpoch, true, 1).TrackKey ==
              new PlaybackReport("Całe lata", "Taco Hemingway", "1-800-OŚWIECENIE", 231_218, 0, DateTimeOffset.UnixEpoch, true, 1).TrackKey,
            "A song stays one song whatever length the player reports for it");

        // Carrying the position forward
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Check(LyricsContract.PositionAt(10_000, at, true, 1, at.AddMilliseconds(2500), 200_000) == 12_500, "Position: a playing track moves on from its last report");
        Check(LyricsContract.PositionAt(10_000, at, false, 1, at.AddSeconds(30), 200_000) == 10_000, "Position: a paused track stays where it was reported");
        Check(LyricsContract.PositionAt(10_000, at, true, 1.5, at.AddSeconds(2), 200_000) == 13_000, "Position: the playback rate is honoured");
        Check(LyricsContract.PositionAt(10_000, at, true, double.NaN, at.AddSeconds(1), 200_000) == 11_000, "Position: an unknown rate counts as normal speed");
        Check(LyricsContract.PositionAt(199_000, at, true, 1, at.AddSeconds(5), 200_000) == 200_000, "Position: never past the end of the track");
        Check(LyricsContract.PositionAt(10_000, at, true, 1, at.AddSeconds(-3), 200_000) == 10_000, "Position: a report from the future doesn't move backwards");
        var report = new PlaybackReport("Song", "Artist", "Album", 200_400, 5_000, at, true, 1);
        Check(report.PositionAt(at.AddMilliseconds(750)) == 5_750 && report.TrackKey == LyricsContract.TrackKey("Song", "Artist", "Album"),
            "PlaybackReport carries forward from its report");

        // Measuring the display's clock (NTP's midpoint)
        var sample = new LyricsContract.ClockSample(1_000, 50_040, 1_080);
        Check(sample.RoundTripMs == 80 && sample.OffsetMs == 49_000, "Clock: the display read its clock halfway through the round trip");
        var best = LyricsContract.BestSample([new(0, 5_100, 300), new(1_000, 6_012, 1_020), new(2_000, 7_030, 2_090)]);
        Check(best?.RoundTripMs == 20 && best?.OffsetMs == 5_002, "Clock: the quickest round trip wins");
        Check(LyricsContract.BestSample([new(0, 1, 900), new(5, 1, 3)]) is null, "Clock: no answer is trusted if every round trip is too slow or impossible");
        Check(LyricsContract.DisplayTime(1_000, 4_294_967_000) == 704 && LyricsContract.DisplayTime(10, -5) == 5, "Clock: display time wraps at 2^32 like the display's own");
        Check(new LyricsContract.ClockSample(100, 3, 120).OffsetMs == -107, "Clock: a display that started after the PC's clock has a negative offset");

        // Reference points
        var anchors = new LyricsAnchors();
        Check(anchors.Update("a", true, 1_000, 0) && anchors.Counter == 1, "Anchors: the first sample is a reference point");
        Check(!anchors.Update("a", true, 2_050, 1_000) && !anchors.Update("a", true, 6_980, 6_000), "Anchors: a player keeping time needs no new point");
        Check(anchors.Update("a", true, 30_000, 7_000) && anchors.PositionMs == 30_000 && anchors.Counter == 2, "Anchors: a seek is a new point");
        Check(anchors.Update("a", false, 30_400, 7_400) && !anchors.Playing, "Anchors: a pause is a new point");
        Check(!anchors.Update("a", false, 30_400, 12_000) && anchors.Update("a", true, 30_400, 12_500), "Anchors: a long pause sends nothing new; resuming does");
        Check(anchors.Update("b", true, 0, 12_600), "Anchors: a new song is a new point");
        Check(anchors.Update("b", true, 10_000, 22_600) && anchors.AtMs == 22_600, "Anchors: a safety resync every ten seconds");
        anchors.Invalidate();
        Check(anchors.Update("b", true, 10_100, 22_700), "Anchors: a new offset or clock forces a point");

        // Protocol
        Check(Protocol.ParseClock("{\"ms\":4294967295}"u8) == uint.MaxValue && Protocol.ParseClock("{\"ms\":-1}"u8) is null &&
              Protocol.ParseClock("{\"ms\":1.5}"u8) is null && Protocol.ParseClock("[1]"u8) is null, "GET /clock: a whole 32-bit millisecond count");
        var music = new MusicStats(true, true, "T", "A", "B", true, 1, 2, true, true, true);
        var withLyrics = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(music with { Lyrics = new LyricsStats(7, "synced", 3, 1234, 4_000_000_000, true) }, Protocol.Json)).RootElement;
        var lyricsJson = withLyrics.GetProperty("lyrics");
        Check(lyricsJson.GetProperty("id").GetInt32() == 7 && lyricsJson.GetProperty("state").GetString() == "synced" && lyricsJson.GetProperty("anchor").GetInt32() == 3 &&
              lyricsJson.GetProperty("pos_ms").GetInt64() == 1234 && lyricsJson.GetProperty("at_ms").GetUInt32() == 4_000_000_000 && lyricsJson.GetProperty("playing").GetBoolean(),
            "Telemetry: music.lyrics carries id, state and the reference point");
        Check(!lyricsJson.TryGetProperty("auto_open", out _) &&
              JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new LyricsStats(7, "synced", 3, 0, 0, true, true), Protocol.Json)).RootElement.GetProperty("auto_open").GetBoolean(),
            "Telemetry: auto_open is sent only while it is on");
        Check(!JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(music, Protocol.Json)).RootElement.TryGetProperty("lyrics", out _),
            "Telemetry: no lyrics object while lyrics are off");
        Check(Enum.GetValues<LyricsState>().Select(LyricsContract.Wire).Distinct().Count() == Enum.GetValues<LyricsState>().Length, "Every lyrics state has its own wire name");

        // Building from LRCLIB's answer
        var found = new LyricsCache.Entry("k", at, true, false, "[00:01.00]One\n[00:02.00]Two", "One\nTwo");
        Check(LyricsService.Build("k", found, Latin) is { State: LyricsState.Synced, Document.Kind: LyricsKind.Synced }, "Build: synced lyrics win over plain");
        Check(LyricsService.Build("k", found with { Synced = null }, Latin) is { State: LyricsState.Plain, Document.Kind: LyricsKind.Plain }, "Build: plain lyrics when there are no timed ones");
        Check(LyricsService.Build("k", found with { Instrumental = true }, Latin).State == LyricsState.Instrumental, "Build: an instrumental says so");
        Check(LyricsService.Build("k", found with { Found = false }, Latin).State == LyricsState.None, "Build: a miss shows no lyrics");

        // Cache
        var cacheDir = Path.Combine(directory, "lyrics-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new LyricsCache(cacheDir);
        cache.Put(found);
        Check(cache.Get("k", at.AddDays(400))?.Synced == found.Synced, "Cache: found lyrics are kept");
        cache.Put(new LyricsCache.Entry("miss", at, false, false, null, null));
        Check(cache.Get("miss", at.AddDays(6)) is { Found: false } && cache.Get("miss", at.AddDays(8)) is null, "Cache: a miss is remembered for a week, then asked again");
        Check(cache.Get("never", at) is null && Directory.GetFiles(cacheDir).All(f => !File.ReadAllText(f).Contains("token", StringComparison.OrdinalIgnoreCase)), "Cache: unknown songs aren't found; files hold lyrics only");
        Directory.Delete(cacheDir, true);

        // LRCLIB client against a recorded conversation
        var requests = new List<HttpRequestMessage>();
        var replies = new Queue<HttpResponseMessage>();
        var client = new LrclibClient(new FakeHandler(requests, replies));
        replies.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        replies.Enqueue(Json("[{\"id\":1,\"trackName\":\"Song\",\"artistName\":\"Artist\",\"duration\":300,\"syncedLyrics\":\"[00:01.00]Wrong edit\"}," +
                             "{\"id\":2,\"trackName\":\"Song\",\"artistName\":\"Artist\",\"duration\":201,\"plainLyrics\":\"Plain\"}," +
                             "{\"id\":3,\"trackName\":\"Song\",\"artistName\":\"Artist\",\"duration\":199,\"syncedLyrics\":\"[00:01.00]Right\"}]"));
        var (outcome, record) = client.FindAsync("Song & Dance", "Artist", "", 200, CancellationToken.None).GetAwaiter().GetResult();
        Check(outcome == LrclibOutcome.Found && record?.Id == 3, "LRCLIB: search keeps only results of the same length and prefers synced lyrics");
        var get = requests[0].RequestUri!;
        Check(get.AbsolutePath == "/api/get" && get.Query.Contains("track_name=Song%20%26%20Dance") && get.Query.Contains("duration=200") && !get.Query.Contains("album_name"),
            "LRCLIB: /api/get escapes the title, sends the length and leaves out an empty album");
        Check(requests[1].RequestUri!.AbsolutePath == "/api/search", "LRCLIB: a 404 falls back to /api/search");
        var agent = requests[0].Headers.UserAgent.ToString();
        Check(agent.StartsWith("ClawPal/") && agent.Contains("https://github.com/Kaayoos/clawpal") && !agent.Contains('@') && !agent.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
            "LRCLIB: the User-Agent names ClawPal and its repository, nothing personal");
        var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        replies.Enqueue(busy);
        Check(client.FindAsync("Other", "Artist", "Album", 100, CancellationToken.None).GetAwaiter().GetResult().Outcome == LrclibOutcome.Failed, "LRCLIB: a 429 is a failure, not a miss");
        int sent = requests.Count;
        Check(client.FindAsync("Third", "Artist", "Album", 100, CancellationToken.None).GetAwaiter().GetResult().Outcome == LrclibOutcome.Failed && requests.Count == sent,
            "LRCLIB: Retry-After is honoured; nothing is sent while waiting");
        Check(LrclibClient.PickSearchResult([new(9, "S", "A", null, 100, false, null, null)], "A", 100) is null, "LRCLIB: a result without lyrics is no result");
        LrclibRecord Lrc(long id, double duration, bool synced = true) => new(id, "Całe lata", "Taco Hemingway", "1-800-OŚWIECENIE", duration, false, synced ? null : "Plain", synced ? "[00:01.00]Line" : null);
        Check(LrclibClient.PickSearchResult([Lrc(1, 258.5), Lrc(2, 237.0), Lrc(3, 285)], "Taco Hemingway", 231)?.Id == 2,
            "LRCLIB: a length shortened by Spotify's crossfade still finds the song, and not a longer edit");
        Check(LrclibClient.PickSearchResult([Lrc(1, 237.0), Lrc(2, 231.5, synced: false)], "Taco Hemingway", 231)?.Id == 2 &&
              LrclibClient.PickSearchResult([Lrc(1, 244.0), Lrc(2, 237.0)], "Taco Hemingway", 231)?.Id == 2,
            "LRCLIB: the reported length wins when it matches; otherwise the nearest longer one");
        Check(LrclibClient.PickSearchResult([Lrc(1, 225.0), Lrc(2, 250.0)], "Taco Hemingway", 231) is null,
            "LRCLIB: a version clearly shorter, or longer than any crossfade, is a different song");
        var fresh = new LrclibClient(new FakeHandler(requests, replies));
        replies.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        replies.Enqueue(Json("[{\"id\":10114803,\"trackName\":\"Całe Lata\",\"artistName\":\"Taco Hemingway\",\"duration\":237.0,\"syncedLyrics\":\"[00:01.00]Line\"}]"));
        Check(fresh.FindAsync("Całe lata", "Taco Hemingway", "1-800-OŚWIECENIE", 231, CancellationToken.None).GetAwaiter().GetResult().Record?.Id == 10114803,
            "LRCLIB: the crossfade case end to end, from a 404 on the exact length to the search");
        return count;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(List<HttpRequestMessage> requests, Queue<HttpResponseMessage> replies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return Task.FromResult(replies.Count > 0 ? replies.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
