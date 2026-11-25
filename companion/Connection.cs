using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Diagnostics;

namespace ClawPal.Companion;

public sealed class Connection : IDisposable
{
    private readonly Configuration config;
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? session;
    private volatile bool paused,maintenance;
    private volatile bool autoOpenApps;
    private volatile bool away;
    // Released to send the next frame now instead of after the usual second.
    private readonly SemaphoreSlim nudge = new(0, 1);
    private readonly ManualResetEventSlim awaySent = new(true);
    private string status = L.Text("starting");
    private long sequence;
    public string Status => L.Retranslate(status);
    public bool IsConnected { get; private set; }
    public DeviceSettingsClient Settings { get; }
    public FirmwareUpdates Updates { get; }
    // 3D printers on the network, read-only; runs whether or not the display is connected.
    public PrinterMonitor Printers { get; }
    private void SetMaintenance(bool value){maintenance=value;IsConnected=false;CancelSession();}
    public Connection(Configuration config) { this.config = config; Settings=new(config); Updates=new(config,Settings,SetMaintenance); paused = config.Paused; autoOpenApps = config.AutoOpenApps;
        Printers = new() { Enabled = () => !paused && config.DisplaySettings?.Enabled(10) != false }; }
    public void SetPaused(bool value) { paused = value; if(value)IsConnected=false; CancelSession(); }
    public void SetAutoOpenApps(bool value) { autoOpenApps = value; }
    // Lock and sleep are sent at once, so the display naps instead of reporting the PC offline.
    public void SetAway(bool value)
    {
        if (away == value) return;
        if (value) awaySent.Reset();
        away = value;
        try { if (nudge.CurrentCount == 0) nudge.Release(); } catch (SemaphoreFullException) { }
    }
    // A saved deck reaches the display with the next frame rather than within a second.
    public void ShortcutsChanged() { try { if (nudge.CurrentCount == 0) nudge.Release(); } catch (SemaphoreFullException) { } }
    // Lets a suspend handler wait briefly until the display has heard before the network goes down.
    public bool WaitAwaySent(TimeSpan timeout) => !IsConnected || awaySent.Wait(timeout);

    public async Task RunAsync()
    {
        try { while (config.Token.Length == 0) await Task.Delay(1000, shutdown.Token); }
        catch (OperationCanceledException) { return; }
        var settingsWorker=Task.Run(()=>Settings.RunAsync(shutdown.Token));
        var updatesWorker=Task.Run(()=>Updates.RunAsync(shutdown.Token));
        using var metrics = new Metrics();
        using var gaming = new GameActivity { Enabled = () => config.TrackFps && (config.DisplaySettings?.Enabled(0) ?? true) };
        var gameWorker = Task.Run(() => gaming.RunAsync(shutdown.Token));
        _ = gameWorker.ContinueWith(task => Log.Write("Game activity worker stopped: " + task.Exception?.GetBaseException().GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
        var media = new MediaSessions { Enabled=()=>config.DisplaySettings?.Enabled(2)??true };
        var mediaWorker = Task.Run(() => media.RunAsync(shutdown.Token));
        // Synced lyrics: looked up on LRCLIB only while switched on, and sent to the display once per song.
        bool LyricsWanted() => config.SyncedLyrics && (config.DisplaySettings?.Enabled(2) ?? true);
        var lyrics = new LyricsService { Enabled = LyricsWanted };
        var lyricsWorker = Task.Run(() => lyrics.RunAsync(media, shutdown.Token));
        _ = lyricsWorker.ContinueWith(task => Log.Write("Lyrics worker stopped: " + task.Exception?.GetBaseException().GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
        // A play, pause, seek or new song reaches the display at once rather than within a second.
        void Nudge() { try { if (nudge.CurrentCount == 0) nudge.Release(); } catch (SemaphoreFullException) { } }
        media.PlaybackChanged += () => { if (LyricsWanted()) Nudge(); };
        lyrics.Changed += Nudge;
        // Channel IDs outlive a reconnect, so the display's faders keep their places.
        var mixer = new AudioMixer();
        var claude = new ClaudeUsage { Enabled = () => config.DisplaySettings?.Enabled(7) ?? true, LiveUsageEnabled = () => config.ClaudeLiveUsage };
        var claudeWorker = Task.Run(() => claude.RunAsync(shutdown.Token));
        var codex = new CodexUsage { Enabled=()=>config.DisplaySettings?.Enabled(4)??true };
        var codexWorker = Task.Run(() => codex.RunAsync(shutdown.Token));
        _ = codexWorker.ContinueWith(task => Log.Write("Codex usage worker stopped: " + task.Exception?.GetBaseException().GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
        var activity = new CodexActivity { Enabled=()=>config.DisplaySettings?.Enabled(4)??true };
        var activityWorker = Task.Run(() => activity.RunAsync(shutdown.Token));
        _ = activityWorker.ContinueWith(task => Log.Write("Codex activity worker stopped: " + task.Exception?.GetBaseException().GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
        var printerWorker = Task.Run(() => Printers.RunAsync(shutdown.Token));
        Log.Write($"Companion started; GPU source: {metrics.GpuProviderName}. {metrics.CpuTemperatureStatus}");
        var retrySeconds = 1;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (maintenance || paused || config.Token.Length == 0)
                {
                    SetStatus(maintenance ? L.Text("updating_display") : paused ? L.Text("paused") : L.Text("pairing_required"));
                    await Task.Delay(1000, shutdown.Token);
                    continue;
                }
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                session = attempt;
                SetStatus(L.Text("looking_for_buddy"));
                var endpoint = await DiscoverAsync(config, attempt.Token);
                if (endpoint is null) throw new IOException(L.Text("buddy_is_not_yet_visible_on_this_network"));
                SetStatus(L.Text("connecting"));
                using var socket = await ConnectAsync(endpoint, config.Token, attempt.Token);
                if (config.LastAddress != endpoint.Address.ToString()) { config.LastAddress = endpoint.Address.ToString(); config.Save(); }
                retrySeconds = 1;
                IsConnected=true; SetStatus(L.Text("connected_0", endpoint.Address));
                long lastResponse = Stopwatch.GetTimestamp();
                var acknowledgements = new Acknowledgements();
                var controls = new MusicControlQueue();
                string DisplayLanguage() => config.DisplaySettings?.Language ?? "en";
                ShortcutDeck Deck() => config.Shortcuts ?? ShortcutDeck.Starter(DisplayLanguage());
                var shortcuts = new ShortcutRunner(Deck, DisplayLanguage, () => away, media);
                var controlWorker = controls.RunAsync(media, WindowFocus.Execute, attempt.Token, shortcuts.ExecuteAsync, mixer.Execute);
                // App icons for the Audio app: one upload per channel per connection, again if it leaves and returns.
                var uploadedIcons = new HashSet<int>();
                bool iconsSupported = true;
                long iconRetryAt = 0;
                Task<bool?>? iconTask = null;
                int iconId = -1;
                // Like the cover, the deck lives in the display's memory, so a reconnect sends it again.
                int uploadedDeck = 0;
                bool shortcutStateLogged = false;
                bool deckSupported = true;
                long deckRetryAt = 0;
                Task<bool?>? deckTask = null;
                // The display keeps one cover; a reconnect may follow its restart, so send it again.
                int uploadedArt = 0;
                bool artSupported = true;
                long artRetryAt = 0;
                Task<bool?>? artTask = null;
                // Lyrics live in the display's memory too. Its clock is measured at connect and
                // every two minutes after; firmware without /clock or /lyrics stops both.
                bool lyricsSupported = true;
                long? clockOffset = null; long clockSyncAt = 0; int sentOffset = config.LyricsOffsetMs;
                Task<ClockResult>? clockTask = null;
                var anchors = new LyricsAnchors();
                int uploadedLyrics = 0;
                long lyricsRetryAt = 0;
                Task<bool?>? lyricsTask = null;
                var receiver =ReceiveAsync(socket, acknowledgements, controls, () => Interlocked.Exchange(ref lastResponse, Stopwatch.GetTimestamp()), attempt.Token);
                try
                {
                    while (!attempt.IsCancellationRequested)
                    {
                        var controlResult = controls.Result;
                        var deck = Deck();
                        int deckRev = deck.Revision(DisplayLanguage()), lit = 0;
                        try { lit = ShortcutRunner.OnMask(deck); }
                        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException)
                        {
                            // Once per connection: the same failure every second says nothing new.
                            if (!shortcutStateLogged) Log.Write("Shortcut state unavailable: " + error);
                            shortcutStateLogged = true;
                        }
                        if (clockTask is { IsCompleted: true })
                        {
                            var clock = await clockTask;
                            clockTask = null;
                            if (!clock.Supported) lyricsSupported = false;
                            else if (clock.OffsetMs is { } measured)
                            {
                                // A clock that moved more than a few milliseconds needs a fresh reference point.
                                if (clockOffset is not { } known || Math.Abs(known - measured) > 15) anchors.Invalidate();
                                clockOffset = measured;
                                clockSyncAt = MonotonicClock.NowMs + 120_000;
                            }
                            else clockSyncAt = MonotonicClock.NowMs + 10_000;
                        }
                        bool lyricsOn = lyricsSupported && LyricsWanted();
                        // Lyrics' requests wait until the cover, Shortcuts and icons are through: a display
                        // allows only a few connections, and too many at once can push out the telemetry socket.
                        bool uploadsIdle = artTask is null && deckTask is null && iconTask is null;
                        if (lyricsOn && uploadsIdle && clockTask is null && lyricsTask is null && MonotonicClock.NowMs >= clockSyncAt)
                            clockTask = SyncClockAsync(attempt.Token);
                        var music = media.Snapshot;
                        LyricsStats? lyricsStats = null;
                        LyricsDocument? lyricsDocument = null;
                        if (lyricsOn && music.Available && media.Playback is { } playback)
                        {
                            var found = lyrics.For(playback.TrackKey);
                            lyricsDocument = found.Document;
                            int offsetMs = Math.Clamp(config.LyricsOffsetMs, -LyricsContract.MaxOffsetMs, LyricsContract.MaxOffsetMs);
                            if (offsetMs != sentOffset) { sentOffset = offsetMs; anchors.Invalidate(); }
                            var state = found.State;
                            if (clockOffset is { } offset)
                            {
                                long pcNow = MonotonicClock.NowMs;
                                anchors.Update(playback.TrackKey, playback.Playing, playback.PositionAt(DateTimeOffset.UtcNow), pcNow);
                                lyricsStats = new LyricsStats(lyricsDocument?.Id ?? 0, LyricsContract.Wire(state), anchors.Counter,
                                    Math.Clamp(anchors.PositionMs + offsetMs, 0, LyricsContract.MaxPositionMs), LyricsContract.DisplayTime(anchors.AtMs, offset), anchors.Playing,
                                    config.LyricsAutoOpen);
                            }
                            // Until the display's clock is known, synced lines can't be placed yet.
                            else lyricsStats = new LyricsStats(lyricsDocument?.Id ?? 0, LyricsContract.Wire(state == LyricsState.Synced ? LyricsState.Loading : state), 0, 0, 0, playback.Playing,
                                config.LyricsAutoOpen);
                        }
                        var sample = metrics.Sample(Interlocked.Increment(ref sequence)) with
                        {
                            Music = music with { ControlId = controlResult.Id, ControlOk = controlResult.Ok, ControlError = controlResult.Error, Lyrics = lyricsStats },
                            Codex = codex.Snapshot with { Activity = activity.Snapshot },
                            Claude = config.DisplaySettings?.Enabled(7)!=false && Settings.FirmwareVersion.Length>0 && Version.TryParse(Settings.FirmwareVersion,out var firmwareVersion) && firmwareVersion>=new Version(0,7,0) ? claude.Snapshot : null,
                            AutoOpenApps = autoOpenApps,
                            Away = away ? true : null,
                            Shortcuts = deckSupported ? new ShortcutStats(deckRev, lit) : null,
                            Mixer = config.DisplaySettings?.Enabled(9) != false ? MixerSnapshot(mixer) : null,
                            Printer = Printers.Snapshot()
                        };
                        gaming.SetGpuUsage(sample.Gpu.Usage);
                        sample = sample with { Game = gaming.Snapshot };
                        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
                        sendTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                        int limit = sample.Claude is null ? 4096 : 6144;
                        var payload = JsonSerializer.SerializeToUtf8Bytes(sample, Protocol.Json);
                        // The mixer gives way first: apps drop off its end until the frame fits.
                        while (payload.Length > limit && sample.Mixer is { } trimmed)
                        {
                            sample = sample with { Mixer = trimmed.Channels.Count > 2 ? new MixerStats(trimmed.Channels.Where((_, i) => i != trimmed.Channels.Count - 2).ToList()) : null };
                            payload = JsonSerializer.SerializeToUtf8Bytes(sample, Protocol.Json);
                        }
                        // Then printers, from the quietest end of the list.
                        while (payload.Length > limit && sample.Printer is { Printers.Count: > 0 } printers)
                        {
                            sample = sample with { Printer = printers with { Printers = printers.Printers.Take(printers.Printers.Count - 1).ToList() } };
                            payload = JsonSerializer.SerializeToUtf8Bytes(sample, Protocol.Json);
                        }
                        if (payload.Length > limit) throw new IOException(L.Text("telemetry_exceeds_the_device_message_limit"));
                        acknowledgements.RecordSent(sample.Seq);
                        await socket.SendAsync(payload, WebSocketMessageType.Text, true, sendTimeout.Token);
                        if (sample.Away == true) awaySent.Set();
                        if (artTask is { IsCompleted: true })
                        {
                            var uploaded = await artTask;
                            if (uploaded is null) artSupported = false;
                            else if (uploaded == false) { uploadedArt = 0; artRetryAt = Environment.TickCount64 + 30000; }
                            artTask = null;
                        }
                        if (artTask is null && artSupported && media.Cover is { } cover && sample.Music?.ArtId == cover.Id &&
                            cover.Id != uploadedArt && Environment.TickCount64 >= artRetryAt)
                        {
                            uploadedArt = cover.Id;
                            artTask = UploadArtAsync(cover, attempt.Token);
                        }
                        if (lyricsTask is { IsCompleted: true })
                        {
                            var uploaded = await lyricsTask;
                            if (uploaded is null) lyricsSupported = false;
                            else if (uploaded == false) { uploadedLyrics = 0; lyricsRetryAt = Environment.TickCount64 + 30000; }
                            lyricsTask = null;
                        }
                        if (lyricsTask is null && clockTask is null && artTask is null && deckTask is null && iconTask is null &&
                            lyricsSupported && lyricsStats is not null && lyricsDocument is { } document &&
                            document.Id != uploadedLyrics && Environment.TickCount64 >= lyricsRetryAt)
                        {
                            uploadedLyrics = document.Id;
                            lyricsTask = UploadLyricsAsync(document, attempt.Token);
                        }
                        if (deckTask is { IsCompleted: true })
                        {
                            var uploaded = await deckTask;
                            if (uploaded is null) deckSupported = false;
                            else if (uploaded == false) { uploadedDeck = 0; deckRetryAt = Environment.TickCount64 + 30000; }
                            deckTask = null;
                        }
                        if (deckTask is null && deckSupported && deckRev != uploadedDeck && Environment.TickCount64 >= deckRetryAt)
                        {
                            uploadedDeck = deckRev;
                            deckTask = UploadShortcutsAsync(deck, DisplayLanguage(), attempt.Token);
                        }
                        if (iconTask is { IsCompleted: true })
                        {
                            var uploaded = await iconTask;
                            if (uploaded is null) iconsSupported = false;
                            else if (uploaded == false) { uploadedIcons.Remove(iconId); iconRetryAt = Environment.TickCount64 + 30000; }
                            iconTask = null;
                        }
                        if (sample.Mixer is { } shown)
                        {
                            // A channel that left may lose its icon on the display; send it again when it returns.
                            uploadedIcons.IntersectWith(shown.Channels.Select(channel => channel.Id));
                            if (iconTask is null && iconsSupported && Environment.TickCount64 >= iconRetryAt &&
                                shown.Channels.FirstOrDefault(channel => channel.Kind == MixerContract.App && !uploadedIcons.Contains(channel.Id)) is { } next)
                            {
                                uploadedIcons.Add(next.Id);
                                iconId = next.Id;
                                if (mixer.IconPaths.TryGetValue(next.Id, out var path)) iconTask = UploadMixerIconAsync(next.Id, path, attempt.Token);
                            }
                        }
                        var delay = nudge.WaitAsync(1000, attempt.Token);
                        if (await Task.WhenAny(receiver, delay) == receiver) { await receiver; throw new IOException(L.Text("buddy_closed_its_connection")); }
                        await delay;
                        var responseAt = Interlocked.Read(ref lastResponse);
                        if (Stopwatch.GetElapsedTime(responseAt) > TimeSpan.FromSeconds(10)) throw new IOException(L.Text("buddy_did_not_acknowledge_telemetry_within_10_seconds"));
                    }
                }
                finally
                {
                    attempt.Cancel();
                    try { await receiver; } catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { }
                    try { await controlWorker; } catch (OperationCanceledException) { }
                    if (artTask is not null) try { await artTask; } catch (OperationCanceledException) { }
                    if (deckTask is not null) try { await deckTask; } catch (OperationCanceledException) { }
                    if (iconTask is not null) try { await iconTask; } catch (OperationCanceledException) { }
                    if (lyricsTask is not null) try { await lyricsTask; } catch (OperationCanceledException) { }
                    if (clockTask is not null) try { await clockTask; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (paused) { }
            catch (Exception error) when (error is IOException or SocketException or WebSocketException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                SetStatus(L.Text("waiting_for_buddy"));
                Log.Write($"Connection retry: {error.GetType().Name}: {error.Message}");
            }
            finally { IsConnected=false; session = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), shutdown.Token); }
            catch (OperationCanceledException) { break; }
            retrySeconds = Math.Min(10, retrySeconds * 2);
        }
        try { await mediaWorker; } catch (OperationCanceledException) { }
        try { await lyricsWorker; } catch (OperationCanceledException) { }
        try { await codexWorker; } catch (OperationCanceledException) { }
        try { await claudeWorker; } catch (OperationCanceledException) { }
        try { await activityWorker; } catch (OperationCanceledException) { }
        try { await printerWorker; } catch (OperationCanceledException) { }
        try { await settingsWorker; } catch(OperationCanceledException) {}
        try { await updatesWorker; } catch(OperationCanceledException) {}
        SetStatus(L.Text("stopped"));
    }

    private void SetStatus(string value) { if (status == value) return; status = value; Log.Write(value); }

    // Covers go over signed HTTP rather than the telemetry socket: one is about 50 KB.
    // Returns null when the firmware predates covers, so this connection stops trying.
    private async Task<bool?> UploadArtAsync(AlbumCover cover, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await DiscoverAsync(config, cancellationToken);
            if (endpoint is null) return false;
            using var http = DeviceAuth.For(endpoint, config.Token).CreateClient(TimeSpan.FromSeconds(10));
            using var content = new ByteArrayContent(cover.Pixels);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{endpoint.Address}:{Protocol.MonitorPort}/art") { Content = content };
            request.Headers.Add("X-ClawPal-Art-Id", cover.Id.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-ClawPal-Art-Size", cover.Size.ToString(CultureInfo.InvariantCulture));
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or SocketException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Write("Album cover upload failed: " + error.GetType().Name);
            return false;
        }
    }
    // One song's lyrics, sent once; telemetry then only carries reference points. Returns null
    // when the firmware predates lyrics, so this connection stops trying.
    private async Task<bool?> UploadLyricsAsync(LyricsDocument document, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await DiscoverAsync(config, cancellationToken);
            if (endpoint is null) return false;
            using var http = DeviceAuth.For(endpoint, config.Token).CreateClient(TimeSpan.FromSeconds(10));
            using var content = new ByteArrayContent(LyricsContract.DisplayJson(document));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.PostAsync($"http://{endpoint.Address}:{Protocol.MonitorPort}/lyrics", content, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or SocketException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Write("Lyrics upload failed: " + error.GetType().Name);
            return false;
        }
    }

    // Measures the display's clock the way NTP does: several signed GET /clock requests on one
    // kept-alive connection, timed on the PC; the quickest round trip gives the offset. The first
    // request opens the connection and isn't counted, so its handshake can't skew the midpoint.
    private async Task<ClockResult> SyncClockAsync(CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await DiscoverAsync(config, cancellationToken);
            if (endpoint is null) return new(true, null);
            using var http = DeviceAuth.For(endpoint, config.Token).CreateClient(TimeSpan.FromSeconds(4));
            var samples = new List<LyricsContract.ClockSample>();
            for (int i = 0; i < 6; ++i)
            {
                long sent = MonotonicClock.NowMs;
                using var response = await http.GetAsync($"http://{endpoint.Address}:{Protocol.MonitorPort}/clock", cancellationToken);
                long received = MonotonicClock.NowMs;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return new(false, null);
                if (!response.IsSuccessStatusCode) break;
                var display = Protocol.ParseClock(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                if (display is null) break;
                if (i > 0) samples.Add(new(sent, display.Value, received));
            }
            if (LyricsContract.BestSample(samples) is not { } best) return new(true, null);
            Log.Write($"Display clock measured: round trip {best.RoundTripMs} ms over {samples.Count} samples.");
            return new(true, best.OffsetMs);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or SocketException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Write("Display clock measurement failed: " + error.GetType().Name);
            return new(true, null);
        }
    }
    private readonly record struct ClockResult(bool Supported, long? OffsetMs);

    // The deck's looks, then each app tile's own icon. Returns null when the firmware has no
    // Shortcuts, so this connection stops trying, and false when it should try again later.
    private async Task<bool?> UploadShortcutsAsync(ShortcutDeck deck, string displayLanguage, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await DiscoverAsync(config, cancellationToken);
            if (endpoint is null) return false;
            using var http = DeviceAuth.For(endpoint, config.Token).CreateClient(TimeSpan.FromSeconds(10));
            using (var content = new ByteArrayContent(deck.DisplayJson(displayLanguage)))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await http.PostAsync($"http://{endpoint.Address}:{Protocol.MonitorPort}/shortcuts", content, cancellationToken);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
                if (!response.IsSuccessStatusCode) return false;
            }
            int rev = deck.Revision(displayLanguage);
            foreach (var tile in deck.Tiles.Where(t => t.UsesAppIcon))
            {
                if (AppIcons.DisplayPixels(tile.Target) is not { } pixels) continue;
                using var content = new ByteArrayContent(pixels);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{endpoint.Address}:{Protocol.MonitorPort}/shortcut-icon") { Content = content };
                request.Headers.Add("X-ClawPal-Shortcut-Rev", rev.ToString(CultureInfo.InvariantCulture));
                request.Headers.Add("X-ClawPal-Shortcut-Slot", tile.Slot.ToString(CultureInfo.InvariantCulture));
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or SocketException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Write("Shortcuts upload failed: " + error.GetType().Name);
            return false;
        }
    }
    private static MixerStats? MixerSnapshot(AudioMixer mixer)
    {
        try { return mixer.Snapshot(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Write("Audio mixer unavailable: " + error.GetType().Name);
            return null;
        }
    }

    // An Audio channel's app icon. Returns null when the firmware has no Audio app, so this
    // connection stops trying, and true when there was no icon to send.
    private async Task<bool?> UploadMixerIconAsync(int id, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (await Task.Run(() => AppIcons.DisplayPixels(path), cancellationToken) is not { } pixels) return true;
            var endpoint = await DiscoverAsync(config, cancellationToken);
            if (endpoint is null) return false;
            using var http = DeviceAuth.For(endpoint, config.Token).CreateClient(TimeSpan.FromSeconds(10));
            using var content = new ByteArrayContent(pixels);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{endpoint.Address}:{Protocol.MonitorPort}/mixer-icon") { Content = content };
            request.Headers.Add("X-ClawPal-Mixer-Id", id.ToString(CultureInfo.InvariantCulture));
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or SocketException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Write("Mixer icon upload failed: " + error.GetType().Name);
            return false;
        }
    }

    public static async Task<ClientWebSocket> ConnectAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", DeviceAuth.For(endpoint, token).Authorization("GET", "/monitor", DeviceAuth.EmptyBodyHash));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        socket.Options.Proxy = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await socket.ConnectAsync(new Uri($"ws://{endpoint.Address}:{Protocol.MonitorPort}/monitor"), timeout.Token); return socket; }
        catch { socket.Dispose(); throw; }
    }

    public static async Task<DeviceEndpoint?> DiscoverAsync(Configuration config, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var nonce = Protocol.NewNonce();
        var request = Protocol.DiscoveryRequest(nonce);
        var targets = BroadcastAddresses();
        if (IPAddress.TryParse(config.LastAddress, out var previous) && Protocol.IsLanAddress(previous)) targets.Insert(0, previous);
        foreach (var target in targets.Distinct())
        {
            try { await udp.SendAsync(request, new IPEndPoint(target, Protocol.DiscoveryPort), cancellationToken); }
            catch (SocketException) { }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(2200));
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                if (packet.RemoteEndPoint.Port != Protocol.DiscoveryPort) continue;
                var endpoint = Protocol.ValidateHello(packet.Buffer, packet.RemoteEndPoint.Address, config.Token, nonce, config.DeviceId);
                if (endpoint is not null) return endpoint;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static List<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !Protocol.IsLanAddress(unicast.Address)) continue;
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    result.Add(new IPAddress(address.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
                }
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }

    public static async Task<byte[]> ReadTextMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var message = new TextMessageBuffer();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            var complete = message.AddFrame(buffer.AsSpan(0, received.Count), received.MessageType, received.EndOfMessage);
            if (complete is not null) return complete;
        }
    }
    private static async Task ReceiveAsync(ClientWebSocket socket, Acknowledgements acknowledgements, MusicControlQueue controls, Action onResponse, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await ReadTextMessageAsync(socket, cancellationToken);
            if (Protocol.ParseControl(response) is { } command)
            {
                if (!controls.Enqueue(command)) throw new IOException(L.Text("buddy_replayed_or_reordered_a_media_command"));
                continue;
            }
            if (!acknowledgements.Accept(response)) throw new IOException(L.Text("buddy_sent_an_invalid_or_unexpected_acknowledgement"));
            onResponse();
        }
    }
    private void CancelSession() { try { session?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() { shutdown.Cancel(); CancelSession(); }
}
