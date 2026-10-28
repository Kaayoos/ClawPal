using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace ClawPal.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Contains("--claude-event")||args.Contains("--claude-statusline"))return ClaudeBridge.Receive(args.Contains("--claude-statusline"));
        try
        {
            if (Value(args,"--render-settings") is { } settingsPreview) {
                L.SetLanguage(Value(args,"--language")); ApplicationConfiguration.Initialize(); var previewConfig=new Configuration { CompanionLanguage=L.Language,DisplaySettings=new DeviceSettings { Language=L.Language,City="Warsaw",Latitude=52.2297,Longitude=21.0122,Timezone="CET-1CEST,M3.5.0,M10.5.0/3" } };
                using var previewConnection=new Connection(previewConfig);using var form=new SettingsForm(previewConfig,previewConnection,true);form.RenderPreviews(settingsPreview);return 0;
            }
            if (Value(args, "--render-preview") is { } previewPath) {
                L.SetLanguage(Value(args,"--language")); ApplicationConfiguration.Initialize();
                var previewConfig = new Configuration{CompanionLanguage=L.Language,DisplaySettings=new DeviceSettings{Language=L.Language}};
                using var previewConnection = new Connection(previewConfig);
                using var form = new SetupForm(previewConfig, previewConnection, true);
                form.RenderPreviews(previewPath); return 0;
            }
            var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? L.Text("unknown");
            Log.Write($"Launch version={version} path={Environment.ProcessPath} data={Configuration.DirectoryPath} origin={StartupContract.Origin(args)}.");
            if (args.Contains("--uninstall")) {
                ApplicationConfiguration.Initialize();
                try { L.SetLanguage(Configuration.Load(recover: false).CompanionLanguage); }
                catch (Exception error) when (error is IOException or JsonException or ArgumentException or FormatException or InvalidOperationException or NotSupportedException) { }
                return Uninstaller.Run(args.Contains("--quiet"));
            }
            if(args.Length==0)L.SetLanguage(Configuration.Load().CompanionLanguage);
            CompanionUpgrade.ApplyIfNewer(args);
            if (StartupContract.ShouldRedirect(args, Environment.ProcessPath, Startup.InstalledPath, File.Exists(Startup.InstalledPath)))
            {
                var start = new ProcessStartInfo(Startup.InstalledPath)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(Startup.InstalledPath)!
                };
                if (args.Contains(StartupContract.StartupArgument)) start.ArgumentList.Add(StartupContract.StartupArgument);
                using var installed = Process.Start(start) ?? throw new IOException(L.Text("could_not_launch_the_installed_companion"));
                Log.Write("Launch redirected to installed companion.");
                return 0;
            }
            if (Value(args, "--diagnostics") is { } diagnosticPath) { Diagnostics(diagnosticPath).GetAwaiter().GetResult(); return 0; }
            if (Value(args, "--music-diagnostics") is { } musicPath) { MusicDiagnostics(musicPath, Value(args, "--music-command")).GetAwaiter().GetResult(); return 0; }
            if (Value(args, "--claude-diagnostics") is { } claudePath) {
                var usageConfig=Configuration.Load();
                using var usageTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var stats=new ClaudeUsage {Enabled=()=>usageConfig.DisplaySettings?.Enabled(7)??true,LiveUsageEnabled=()=>usageConfig.ClaudeLiveUsage}.RefreshAsync(usageTimeout.Token).GetAwaiter().GetResult();
                var safe=new { stats.Running,stats.Available,stats.Stale,stats.Status,stats.AgeS,stats.Primary,stats.Secondary,
                    WorkingCount=stats.Activity?.WorkingCount??0,TaskCount=stats.Activity?.TotalCount??0 };
                File.WriteAllText(claudePath,JsonSerializer.Serialize(safe,Protocol.Json));return 0;
            }
            if (Value(args, "--codex-diagnostics") is { } codexPath) { CodexDiagnostics(codexPath).GetAwaiter().GetResult(); return 0; }
            if (Value(args, "--activity-diagnostics") is { } activityPath) { ActivityDiagnostics(activityPath).GetAwaiter().GetResult(); return 0; }
            if (Value(args, "--game-diagnostics") is { } gamePath) {
                using var provider = new GameActivity();
                var result = provider.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(gamePath, JsonSerializer.Serialize(result, Protocol.Json)); return 0;
            }
            var config = Configuration.Load();
            L.SetLanguage(config.CompanionLanguage);
            if (Value(args,"--sync-settings") is { } syncPath) {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                new DeviceSettingsClient(config).SyncAsync(timeout.Token).GetAwaiter().GetResult();
                File.WriteAllText(syncPath,JsonSerializer.Serialize(new { pending=config.SettingsPending }));return 0;
            }
            if(Value(args,"--prepare-wifi-updates") is { } usbPort) {
                using var connection=new Connection(config);
                connection.Updates.PrepareUsb(usbPort,new Progress<string>(Log.Write),CancellationToken.None).GetAwaiter().GetResult();return 0;
            }
            if(Value(args,"--install-bundled-wifi") is { } resultPath) {
                using var connection=new Connection(config);using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                connection.Updates.Install(true,timeout.Token).GetAwaiter().GetResult();
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new {status=connection.Updates.Status,device=connection.Updates.Device}));return 0;
            }
            if (config.Token.Length > 0) Installer.PreparePayload(config.BoardId);
            Log.Write($"Configuration loaded paired={Protocol.IsLowerHex(config.Token, 64) && Protocol.IsLowerHex(config.DeviceId, 12)} paused={config.Paused}.");
            if (Value(args, "--pair-token-file") is { } tokenFile) config.Pair(tokenFile, Value(args, "--device-id") ?? throw new ArgumentException("--device-id is required when pairing."));
            if (args.Contains("--set-startup")) Startup.Set(true);
            if (args.Contains("--remove-startup")) Startup.Set(false);
            if (args.Contains("--disconnect-claude")) { ClaudeBridge.Uninstall(); config.ClaudeLiveUsage=false; config.Save(); return 0; }
            if (args.Contains("--create-start-menu")) Startup.CreateStartMenuShortcut();
            if (args.Contains("--configure-only")) return 0;
            if (Value(args, "--test-connection") is { } connectionPath) { TestConnection(config, connectionPath).GetAwaiter().GetResult(); return 0; }
            using var singleton = new Mutex(true, @"Local\ClawPal.Companion", out var firstInstance);
            if (!firstInstance) { using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClawPal.Show"); signal.Set(); return 0; }
            // Keeps the installed-apps entry, and its version, current after every upgrade.
            if (StartupContract.SamePath(Environment.ProcessPath, Startup.InstalledPath))
                try { Uninstaller.Register(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Write("Uninstall entry unavailable: " + error.GetType().Name); }
            Installer.DeleteStaleProvisioningImages();
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            using var context = new TrayContext(config, !args.Contains(StartupContract.StartupArgument) || Configuration.Recovered);
            Application.Run(context);
            return 0;
        }
        catch (Exception error) { Log.Write($"Startup failed: {error.GetType().Name}: {error.Message}"); if(args.Any(a=>a is "--sync-settings" or "--prepare-wifi-updates" or "--install-bundled-wifi")){Console.Error.WriteLine(error.Message);return 1;} MessageBox.Show(error.Message, L.Text("clawpal_could_not_start"), MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    private static string? Value(string[] args, string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static async Task Diagnostics(string path)
    {
        using var metrics = new Metrics();
        metrics.Sample(0);
        await Task.Delay(1200);
        var sample = metrics.Sample(1);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { stats = sample, gpu_provider = metrics.GpuProviderName, cpu_temperature_status = metrics.CpuTemperatureStatus }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
    }
    private static async Task MusicDiagnostics(string path, string? action)
    {
        // Valued commands are written as seek:SECONDS or volume:PERCENT.
        string[] parts = (action ?? "").Split(':', 2);
        int value = 0;
        bool valid = action is null ||
            (parts.Length == 1 && parts[0] is "play_pause" or "next" or "previous" or "shuffle" or "repeat" or "mute") ||
            (parts.Length == 2 && parts[0] is "seek" or "volume" && int.TryParse(parts[1], out value) && value >= 0 && value <= (parts[0] == "volume" ? 100 : 604800));
        if (!valid) throw new ArgumentException(L.Text("unsupported_spotify_diagnostic_command"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var media = new MediaSessions();
        var before = await media.RefreshAsync(timeout.Token);
        MusicControlResult? command = null;
        if (action is not null)
        {
            command = await media.ExecuteAsync(new(1, parts[0], value), timeout.Token);
            await Task.Delay(500, timeout.Token);
        }
        var after = action is null ? before : await media.RefreshAsync(timeout.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { provider = "Windows SMTC, any player", status = media.ProviderStatus, cover = media.Cover is { } cover ? new { cover.Id, cover.Size } : null, before, command, after }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }), timeout.Token);
    }
    private static async Task CodexDiagnostics(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var provider = new CodexUsage();
        var stats = await provider.DiagnoseAsync(timeout.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { provider = "Codex app-server read-only", stage = provider.DiagnosticStage, error = provider.DiagnosticError, stats }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }), timeout.Token);
    }
    private static async Task ActivityDiagnostics(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var provider = new CodexActivity();
        var activity = await provider.DiagnoseAsync(timeout.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { provider = "Recorded local Codex activity", error = provider.DiagnosticError, activity }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }), timeout.Token);
    }
    private static async Task TestConnection(Configuration config, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var endpoint = await Connection.DiscoverAsync(config, timeout.Token) ?? throw new IOException(L.Text("buddy_discovery_timed_out"));
        using var socket = await Connection.ConnectAsync(endpoint, config.Token, timeout.Token);
        using var metrics = new Metrics();
        metrics.Sample(0); await Task.Delay(1100, timeout.Token);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(metrics.Sample(1), Protocol.Json), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
        var reply = await Connection.ReadTextMessageAsync(socket, timeout.Token);
        if (Protocol.ParseAcknowledgement(reply) != 1) throw new IOException(L.Text("buddy_did_not_return_a_valid_acknowledgement_for_test"));
        var response = System.Text.Encoding.UTF8.GetString(reply);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { connected = true, device_id = endpoint.Id, address = endpoint.Address.ToString(), response }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly Icon artwork;
    private readonly Connection connection;
    private readonly Task worker;
    private readonly System.Windows.Forms.Timer refresh;
    private readonly Configuration config;
    private readonly ToolStripMenuItem status;
    private SetupForm? setup;
    private SettingsForm? settings;
    private readonly EventWaitHandle showSignal = new(false, EventResetMode.AutoReset, @"Local\ClawPal.Show");
    private readonly EventWaitHandle exitSignal = new(false, EventResetMode.AutoReset, Uninstaller.ExitSignal);
    private bool locked;
    public TrayContext(Configuration config, bool show)
    {
        this.config = config;
        Application.ThreadException += (_, e) => { ShowSetup(); if(settings is { IsDisposed:false })settings.ReportProblem(L.Text("unexpected_problem")+e.Exception.Message);else setup?.ReportProblem(L.Text("unexpected_problem") + e.Exception.Message); };
        connection = new Connection(config);
        artwork = MakeIcon();
        var menu = new ContextMenuStrip { BackColor=ClawTheme.Surface, ForeColor=ClawTheme.Ink, Renderer=new ToolStripProfessionalRenderer(new ClawMenuColors()) };
        status = new ToolStripMenuItem(L.Text("clawpal_starting")) { Enabled = false };
        menu.Items.Add(L.Text("open_clawpal"), null, (_, _) => ShowSetup());
        menu.Items.Add(status);
        menu.Opening+=(_,_)=>{foreach(ToolStripItem item in menu.Items)item.Text=L.Retranslate(item.Text??"");};
        var version = typeof(TrayContext).Assembly.GetName().Version?.ToString(3) ?? L.Text("unknown");
        menu.Items.Add(new ToolStripMenuItem(L.Text("companion_version_value", version)) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.Text("exit_clawpal"), null, (_, _) => { if (setup?.Busy == true || connection.Updates.Busy) { ShowSetup(); return; } ExitThread(); });
        icon = new NotifyIcon { Text = L.Text("clawpal_starting"), Icon = artwork, ContextMenuStrip = menu, Visible = true };
        refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        refresh.Tick += (_, _) => {
            var available=connection.Updates.NotificationVersion;
            var text=L.Text("clawpal_dfcb92")+(available is null?connection.Status:L.Text("release_available_short", available));
            status.Text=text;icon.Text=text.Length>63?text[..63]:text;
            if(available is not null){var key=connection.Updates.Repository+"@"+available;
                if(config.UpdateNotificationKey!=key){config.UpdateNotificationKey=key;config.Save();icon.ShowBalloonTip(5000,L.Text("clawpal_update_available"),L.Text("release_update_notice", available),ToolTipIcon.Info);}}
        };
        icon.BalloonTipClicked+=(_,_)=>{ShowSetup();settings?.ShowUpdates();};
        icon.DoubleClick += (_, _) => ShowSetup();
        refresh.Tick += (_, _) => { if (showSignal.WaitOne(0)) ShowSetup(); };
        // The uninstaller asks this app to exit; it never interrupts a display being written.
        refresh.Tick += (_, _) => { if (exitSignal.WaitOne(0) && setup?.Busy != true && !connection.Updates.Busy) ExitThread(); };
        refresh.Start();
        SystemEvents.SessionSwitch += SessionSwitched;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        if (Configuration.Recovered) icon.ShowBalloonTip(8000, L.Text("settings_damaged_title"), L.Text("settings_damaged_body"), ToolTipIcon.Warning);
        if (show) ShowSetup();
        worker = Task.Run(connection.RunAsync);
        _ = worker.ContinueWith(task => Log.Write($"Monitor stopped unexpectedly: {task.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
    }
    // The display would otherwise only see telemetry stop and report the PC offline.
    private void SessionSwitched(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.RemoteConnect) locked = false;
        else return;
        connection.SetAway(locked);
    }
    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { connection.SetAway(true); connection.WaitAwaySent(TimeSpan.FromSeconds(1.5)); }
        else if (e.Mode == PowerModes.Resume) connection.SetAway(locked);
    }
    private void ShowSetup() {
        if(setup?.Busy==true) { setup.Show();setup.Activate();return; }
        if(StartupContract.ShouldShowSettings(config.SetupComplete,config.Token.Length==64)) {
            if(settings is null || settings.IsDisposed) { settings=new SettingsForm(config,connection);settings.OpenInstaller+=ShowInstaller; }
            settings.Show();settings.WindowState=FormWindowState.Normal;settings.Activate();
        } else ShowInstaller();
    }
    private void ShowInstaller() {
        if(connection.Updates.Busy)return;
        if(setup is null || setup.IsDisposed) { setup=new SetupForm(config,connection);setup.OpenSettings+=()=>{setup.Hide();ShowSetup();}; }
        settings?.Hide();setup.Show();setup.WindowState=FormWindowState.Normal;setup.Activate();
    }
    protected override void ExitThreadCore() { connection.Dispose(); icon.Visible = false; base.ExitThreadCore(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { SystemEvents.SessionSwitch -= SessionSwitched; SystemEvents.PowerModeChanged -= PowerModeChanged; refresh.Dispose(); showSignal.Dispose(); exitSignal.Dispose(); setup?.Dispose(); settings?.Dispose(); connection.Dispose(); icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); artwork.Dispose(); }
        base.Dispose(disposing);
    }
    private static Icon MakeIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("ClawPal.Companion.CrabIcon")
            ?? throw new InvalidOperationException(L.Text("the_embedded_crab_icon_is_missing"));
        using var embedded = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)embedded.Clone();
    }
}
