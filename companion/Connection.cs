using System.Net;
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
    private string status = L.Text("starting");
    private long sequence;
    public string Status => L.Retranslate(status);
    public bool IsConnected { get; private set; }
    public DeviceSettingsClient Settings { get; }
    public FirmwareUpdates Updates { get; }
    private void SetMaintenance(bool value){maintenance=value;IsConnected=false;CancelSession();}
    public Connection(Configuration config) { this.config = config; Settings=new(config); Updates=new(config,Settings,SetMaintenance); paused = config.Paused; autoOpenApps = config.AutoOpenApps; }
    public void SetPaused(bool value) { paused = value; if(value)IsConnected=false; CancelSession(); }
    public void SetAutoOpenApps(bool value) { autoOpenApps = value; }

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
        var media = new SpotifyMedia { Enabled=()=>config.DisplaySettings?.Enabled(2)??true };
        var mediaWorker = Task.Run(() => media.RunAsync(shutdown.Token));
        var claude = new ClaudeUsage { Enabled = () => config.DisplaySettings?.Enabled(7) ?? true, LiveUsageEnabled = () => config.ClaudeLiveUsage };
        var claudeWorker = Task.Run(() => claude.RunAsync(shutdown.Token));
        var codex = new CodexUsage { Enabled=()=>config.DisplaySettings?.Enabled(4)??true };
        var codexWorker = Task.Run(() => codex.RunAsync(shutdown.Token));
        _ = codexWorker.ContinueWith(task => Log.Write("Codex usage worker stopped: " + task.Exception?.GetBaseException().GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
        var activity = new CodexActivity { Enabled=()=>config.DisplaySettings?.Enabled(4)??true };
        var activityWorker = Task.Run(() => activity.RunAsync(shutdown.Token));
        _ = activityWorker.ContinueWith(task => Log.Write("Codex activity worker stopped: " + task.Exception?.GetBaseException().GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
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
                var controlWorker = controls.RunAsync(media, attempt.Token);
                var receiver = ReceiveAsync(socket, acknowledgements, controls, () => Interlocked.Exchange(ref lastResponse, Stopwatch.GetTimestamp()), attempt.Token);
                try
                {
                    while (!attempt.IsCancellationRequested)
                    {
                        var controlResult = controls.Result;
                        var sample = metrics.Sample(Interlocked.Increment(ref sequence)) with
                        {
                            Music = media.Snapshot with { ControlId = controlResult.Id, ControlOk = controlResult.Ok, ControlError = controlResult.Error },
                            Codex = codex.Snapshot with { Activity = activity.Snapshot },
                            Claude = config.DisplaySettings?.Enabled(7)!=false && Settings.FirmwareVersion.Length>0 && Version.TryParse(Settings.FirmwareVersion,out var firmwareVersion) && firmwareVersion>=new Version(0,7,0) ? claude.Snapshot : null,
                            AutoOpenApps = autoOpenApps
                        };
                        gaming.SetGpuUsage(sample.Gpu.Usage);
                        sample = sample with { Game = gaming.Snapshot };
                        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
                        sendTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                        var payload = JsonSerializer.SerializeToUtf8Bytes(sample, Protocol.Json);
                        if (payload.Length > (sample.Claude is null ? 4096 : 6144)) throw new IOException(L.Text("telemetry_exceeds_the_device_message_limit"));
                        acknowledgements.RecordSent(sample.Seq);
                        await socket.SendAsync(payload, WebSocketMessageType.Text, true, sendTimeout.Token);
                        var delay = Task.Delay(1000, attempt.Token);
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
        try { await codexWorker; } catch (OperationCanceledException) { }
        try { await claudeWorker; } catch (OperationCanceledException) { }
        try { await activityWorker; } catch (OperationCanceledException) { }
        try { await settingsWorker; } catch(OperationCanceledException) {}
        try { await updatesWorker; } catch(OperationCanceledException) {}
        SetStatus(L.Text("stopped"));
    }

    private void SetStatus(string value) { if (status == value) return; status = value; Log.Write(value); }
    public static async Task<ClientWebSocket> ConnectAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
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
