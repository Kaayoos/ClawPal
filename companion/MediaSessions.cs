using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Control;

namespace ClawPal.Companion;

// A small square cover for the display: RGB565 little-endian, Size x Size pixels.
public sealed record AlbumCover(int Id, int Size, byte[] Pixels);

// Windows SMTC is local and needs no player account or cloud API. Any player that
// publishes a Windows media session works: Spotify, Apple Music, TIDAL, browsers and more.
public sealed class MediaSessions
{
    public const int CoverSize = 160;
    public Func<bool> Enabled { get; init; } = () => true;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private readonly SemaphoreSlim mediaGate = new(1, 1);
    private MusicStats snapshot = Empty(false);
    private string providerStatus = "Starting";
    private string artTrack = "";
    private int artColor, artAttempts;
    private AlbumCover? cover;
    private string? audioProcess;
    public MusicStats Snapshot => Volatile.Read(ref snapshot);
    public AlbumCover? Cover => Volatile.Read(ref cover);
    public string ProviderStatus => providerStatus;
    private static MusicStats Empty(bool running) => new(running, false, "", "", "", false, 0, 0, false, false, false);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RefreshAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // A transient process/COM failure must not permanently freeze cached media.
                var failure = "Media polling failed: " + error.GetType().Name;
                if (providerStatus != failure) Log.Write(failure);
                providerStatus = failure;
                manager = null;
                Volatile.Write(ref snapshot, Empty(false));
            }
            await Task.Delay(1000, cancellationToken);
        }
    }

    public async Task<MusicStats> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Enabled())
        {
            providerStatus = "Music is turned off.";
            Volatile.Write(ref snapshot, Empty(false));
            return Snapshot;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await mediaGate.WaitAsync(timeout.Token);
            try
            {
                var session = await FindSessionAsync(timeout.Token);
                if (session is null)
                {
                    providerStatus = "No player has a Windows media session.";
                    audioProcess = null;
                    Volatile.Write(ref snapshot, Empty(false));
                    return Snapshot;
                }
                var appId = session.SourceAppUserModelId;
                var media = await session.TryGetMediaPropertiesAsync().AsTask(timeout.Token);
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                double position = (timeline.Position - timeline.StartTime).TotalSeconds;
                var duration = MusicContract.Seconds((timeline.EndTime - timeline.StartTime).TotalSeconds);
                if (playing && timeline.LastUpdatedTime >= DateTimeOffset.UnixEpoch)
                {
                    var elapsed = Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds);
                    var rate = playback.PlaybackRate ?? 1;
                    if (double.IsFinite(rate) && rate > 0) position += elapsed * rate;
                }
                var controls = playback.Controls;
                var canPlayPause = controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled);
                var shuffle = playback.IsShuffleActive;
                var repeat = playback.AutoRepeatMode;
                audioProcess = MusicContract.ProcessName(appId);
                var volume = audioProcess is null ? default : AppAudio.Read(audioProcess);
                await ArtAsync(media, timeout.Token);
                var sampled = new MusicStats(true, true, MusicContract.LimitText(media?.Title, 191), MusicContract.LimitText(media?.Artist, 127),
                    MusicContract.LimitText(media?.AlbumTitle, 127), playing, Math.Min(MusicContract.Seconds(position), duration), duration,
                    canPlayPause, controls.IsNextEnabled, controls.IsPreviousEnabled,
                    CanShuffle: controls.IsShuffleEnabled && shuffle.HasValue, Shuffle: shuffle == true,
                    CanRepeat: controls.IsRepeatEnabled && repeat.HasValue, Repeat: Math.Clamp((int)(repeat ?? MediaPlaybackAutoRepeatMode.None), 0, 2),
                    CanSeek: controls.IsPlaybackPositionEnabled && duration > 0,
                    CanVolume: volume.Available, Volume: volume.Volume, Muted: volume.Muted,
                    ArtColor: artColor, Source: MusicContract.SourceName(appId), ArtId: Cover?.Id ?? 0);
                providerStatus = "Windows media session available: " + sampled.Source;
                Volatile.Write(ref snapshot, sampled);
                return sampled;
            }
            finally { mediaGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            manager = null;
            providerStatus = "Windows media data unavailable: " + error.GetType().Name;
            Volatile.Write(ref snapshot, Empty(false));
            return Snapshot;
        }
    }

    public async Task<MusicControlResult> ExecuteAsync(DeviceControl command, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await mediaGate.WaitAsync(timeout.Token);
            try
            {
                if (command.Action is "volume" or "mute")
                {
                    // The mixer session belongs to the player's audio stream, not its media session.
                    bool changed = audioProcess is { } process && (command.Action == "volume" ? AppAudio.SetVolume(process, command.Value) : AppAudio.ToggleMute(process));
                    return new(command.Id, changed, changed ? "" : "unavailable");
                }
                var session = await FindSessionAsync(timeout.Token);
                if (session is null) return new(command.Id, false, "unavailable");
                var playback = session.GetPlaybackInfo();
                var controls = playback.Controls;
                bool ok;
                switch (command.Action)
                {
                    case "play_pause":
                        if (controls.IsPlayPauseToggleEnabled)
                            ok = await session.TryTogglePlayPauseAsync().AsTask(timeout.Token);
                        else if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && controls.IsPauseEnabled)
                            ok = await session.TryPauseAsync().AsTask(timeout.Token);
                        else if (playback.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && controls.IsPlayEnabled)
                            ok = await session.TryPlayAsync().AsTask(timeout.Token);
                        else return new(command.Id, false, "unsupported");
                        break;
                    case "next":
                        if (!controls.IsNextEnabled) return new(command.Id, false, "unsupported");
                        ok = await session.TrySkipNextAsync().AsTask(timeout.Token);
                        break;
                    case "previous":
                        if (!controls.IsPreviousEnabled) return new(command.Id, false, "unsupported");
                        ok = await session.TrySkipPreviousAsync().AsTask(timeout.Token);
                        break;
                    case "shuffle":
                        if (!controls.IsShuffleEnabled) return new(command.Id, false, "unsupported");
                        ok = await session.TryChangeShuffleActiveAsync(playback.IsShuffleActive != true).AsTask(timeout.Token);
                        break;
                    case "repeat":
                        if (!controls.IsRepeatEnabled) return new(command.Id, false, "unsupported");
                        // Spotify's own order, which most players share: off, the whole list, then the current track.
                        var next = playback.AutoRepeatMode switch
                        {
                            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
                            _ => MediaPlaybackAutoRepeatMode.List,
                        };
                        ok = await session.TryChangeAutoRepeatModeAsync(next).AsTask(timeout.Token);
                        break;
                    case "seek":
                        var timeline = session.GetTimelineProperties();
                        if (!controls.IsPlaybackPositionEnabled || timeline.EndTime <= timeline.StartTime) return new(command.Id, false, "unsupported");
                        var target = timeline.StartTime + TimeSpan.FromSeconds(command.Value);
                        ok = await session.TryChangePlaybackPositionAsync((target < timeline.EndTime ? target : timeline.EndTime).Ticks).AsTask(timeout.Token);
                        break;
                    default: return new(command.Id, false, "unsupported");
                }
                return new(command.Id, ok, ok ? "" : "failed");
            }
            finally { mediaGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A failed/timed-out command is never repeated: a skip may already have reached the player.
            return new(command.Id, false, "failed");
        }
    }

    // The cover is decoded in memory once per track. Only its accent color and a small RGB565
    // copy for the paired display leave this method; nothing is stored. Players can publish
    // a track a moment before its cover, so a missing cover is retried for a few polls.
    private async Task ArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? media, CancellationToken cancellationToken)
    {
        string track = media is null ? "" : media.Title + "\n" + media.Artist + "\n" + media.AlbumTitle;
        if (track != artTrack) { artTrack = track; artColor = 0; artAttempts = 0; Volatile.Write(ref cover, null); }
        if (Cover is not null || artAttempts >= 5 || media?.Thumbnail is null) return;
        ++artAttempts;
        try
        {
            using var stream = await media.Thumbnail.OpenReadAsync().AsTask(cancellationToken);
            if (stream.Size == 0 || stream.Size > 8_388_608) return;
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            // Fill the square: scale the short side to the cover size and crop the long side.
            double scale = (double)CoverSize / Math.Min(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            uint width = (uint)Math.Max(CoverSize, Math.Round(decoder.OrientedPixelWidth * scale)), height = (uint)Math.Max(CoverSize, Math.Round(decoder.OrientedPixelHeight * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds { X = (width - CoverSize) / 2u, Y = (height - CoverSize) / 2u, Width = CoverSize, Height = CoverSize },
            };
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            var bgra = pixels.DetachPixelData();
            if (bgra.Length != CoverSize * CoverSize * 4) return;
            artColor = MusicContract.AccentColor(bgra);
            var rgb565 = MusicContract.Rgb565(bgra);
            Volatile.Write(ref cover, new AlbumCover(MusicContract.CoverId(rgb565), CoverSize, rgb565));
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { }
    }

    // A playing session wins, Windows' current session next, then any other.
    private async Task<GlobalSystemMediaTransportControlsSession?> FindSessionAsync(CancellationToken cancellationToken)
    {
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        var sessions = manager.GetSessions().ToArray();
        if (sessions.Length == 0) return null;
        var current = manager.GetCurrentSession();
        static bool Playing(GlobalSystemMediaTransportControlsSession value) =>
            value.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (current is not null && Playing(current)) return current;
        return sessions.FirstOrDefault(Playing) ?? current ?? sessions[0];
    }
}

// One queue per authenticated WebSocket connection. Never replay commands on reconnect.
public sealed class MusicControlQueue
{
    private readonly Channel<DeviceControl> queue = Channel.CreateBounded<DeviceControl>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ControlSequence sequence = new();
    private MusicControlResult result = new(0, false, "");
    public MusicControlResult Result => Volatile.Read(ref result);

    public bool Enqueue(DeviceControl command)
    {
        if (!sequence.Accept(command)) return false;
        if (!queue.Writer.TryWrite(command)) SetResult(new(command.Id, false, "failed"));
        return true;
    }
    private void SetResult(MusicControlResult value)
    {
        MusicControlResult previous;
        do
        {
            previous = Result;
            if (previous.Id >= value.Id) return;
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref result, value, previous), previous));
    }
    // Focus requests from the needs-you alert share the queue, ordering and replay checks.
    public async Task RunAsync(MediaSessions media, Func<DeviceControl, MusicControlResult> focus, CancellationToken cancellationToken)
    {
        await foreach (var command in queue.Reader.ReadAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = command.Action == "focus" ? focus(command) : await media.ExecuteAsync(command, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetResult(outcome);
        }
    }
}
