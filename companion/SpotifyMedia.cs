using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Media.Control;

namespace ClawPal.Companion;

// Windows SMTC is local and needs no Spotify account credentials or cloud API.
// Only the Spotify executable/package identities in MusicContract are selected.
public sealed class SpotifyMedia
{
    public Func<bool> Enabled { get; init; } = () => true;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private readonly SemaphoreSlim mediaGate = new(1, 1);
    private MusicStats snapshot = Empty(false);
    private string providerStatus = "Starting";
    public MusicStats Snapshot => Volatile.Read(ref snapshot);
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
                var failure = "Spotify polling failed: " + error.GetType().Name;
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
        var running = Enabled() && IsSpotifyRunning();
        if (!running)
        {
            providerStatus = "Spotify is not running.";
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
                    providerStatus = running ? "Spotify is running; waiting for its Windows media session." : "Spotify is not running.";
                    Volatile.Write(ref snapshot, Empty(running));
                    return Snapshot;
                }
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
                var sampled = new MusicStats(running, true, MusicContract.LimitText(media?.Title, 191), MusicContract.LimitText(media?.Artist, 127),
                    MusicContract.LimitText(media?.AlbumTitle, 127), playing, Math.Min(MusicContract.Seconds(position), duration), duration,
                    canPlayPause, controls.IsNextEnabled, controls.IsPreviousEnabled);
                providerStatus = "Spotify Windows media session available.";
                Volatile.Write(ref snapshot, sampled);
                return sampled;
            }
            finally { mediaGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            manager = null;
            providerStatus = "Spotify Windows media data unavailable: " + error.GetType().Name;
            Volatile.Write(ref snapshot, Empty(running));
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
                    default: return new(command.Id, false, "unsupported");
                }
                return new(command.Id, ok, ok ? "" : "failed");
            }
            finally { mediaGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A failed/timed-out command is never repeated: a skip may already have reached Spotify.
            return new(command.Id, false, "failed");
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> FindSessionAsync(CancellationToken cancellationToken)
    {
        // Windows can retain a stopped application's media session briefly.
        if (!IsSpotifyRunning()) return null;
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        var spotify = manager.GetSessions().Where(value => MusicContract.IsSpotifySession(value.SourceAppUserModelId)).ToArray();
        return spotify.FirstOrDefault(value => value.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? spotify.FirstOrDefault();
    }

    private static bool IsSpotifyRunning()
    {
        Process[] processes = [];
        try
        {
            using var current = Process.GetCurrentProcess();
            var sessionId = current.SessionId;
            processes = Process.GetProcessesByName("Spotify");
            foreach (var process in processes)
            {
                try { if (process.SessionId == sessionId) return true; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
            }
            return false;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
        finally { foreach (var process in processes) process.Dispose(); }
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
    public async Task RunAsync(SpotifyMedia media, CancellationToken cancellationToken)
    {
        await foreach (var command in queue.Reader.ReadAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await media.ExecuteAsync(command, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetResult(outcome);
        }
    }
}
