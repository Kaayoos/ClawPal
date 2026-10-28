using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClawPal.Companion;

public sealed record FirmwarePart(string File, string Offset, string Sha256);
public sealed record BoardProfile(string Id, string Name, string Chip, int Width, int Height, int FlashBytes, string Version, string ToolSha256, FirmwarePart[] Parts);
public sealed record BackupRecord(string BoardId, string DeviceId, long Bytes, string Sha256, DateTimeOffset Created);
public sealed record SetupOptions(string Port, string Ssid, string Password, string City, double Latitude, double Longitude, string Timezone, bool TrackFps, bool AutoOpen, bool StartWithWindows, bool CreateBackup = true, string CompanionLanguage = "en", string DisplayLanguage = "en", string BoardId = BoardCatalog.DefaultId)
{
    public void Validate()
    {
        BoardCatalog.Get(BoardId);
        if(!L.Supports(CompanionLanguage)||!L.Supports(DisplayLanguage))throw new ArgumentException(L.Text("unsupported_language"));
        if (!Regex.IsMatch(Port, "^COM[0-9]+$")) throw new ArgumentException(L.Text("select_a_connected_usb_serial_port"));
        if (string.IsNullOrWhiteSpace(Ssid) || Encoding.UTF8.GetByteCount(Ssid) > 32 || Ssid.Contains('\0')) throw new ArgumentException(L.Text("enter_a_wi_fi_name_of_1_32_utf"));
        int bytes = Encoding.UTF8.GetByteCount(Password);
        if (Password.Contains('\0') || !(bytes == 0 || bytes is >= 8 and <= 63 || Regex.IsMatch(Password, "^[0-9a-fA-F]{64}$"))) throw new ArgumentException(L.Text("wi_fi_passwords_need_8_63_bytes_leave_blank"));
        if (string.IsNullOrWhiteSpace(City) || Encoding.UTF8.GetByteCount(City) > 63 || City.Contains('\0')) throw new ArgumentException(L.Text("choose_a_location_with_a_name_shorter_than_64"));
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude) || Math.Abs(Latitude) > 90 || Math.Abs(Longitude) > 180) throw new ArgumentException(L.Text("location_coordinates_are_invalid"));
        if (!Regex.IsMatch(Timezone, "^[A-Za-z0-9+,.:/<>-]{3,127}$")) throw new ArgumentException(L.Text("choose_a_supported_time_zone"));
    }
}

public sealed class Installer
{
    private static readonly Lazy<string> bundledVersion=new(()=>{
        using var stream=typeof(Installer).Assembly.GetManifestResourceStream("ClawPal.Payload");
        if(stream is null)return "unavailable";
        using var zip=new ZipArchive(stream,ZipArchiveMode.Read);using var board=zip.GetEntry("board.json")?.Open()??throw new IOException(L.Text("missing_bundled_board_profile"));
        return JsonSerializer.Deserialize<BoardProfile>(board)?.Version??throw new IOException(L.Text("missing_bundled_firmware_version"));
    });
    internal static string BundledVersion=>bundledVersion.Value;
    public static string PayloadPath => Path.Combine(Configuration.DirectoryPath, "payload", typeof(Installer).Assembly.GetName().Version!.ToString(3));
    public static string BackupsPath => Path.Combine(Configuration.DirectoryPath, "backups");
    private readonly IProgress<string> progress;
    private readonly Func<BoardProfile>? testProfile;
    private readonly Func<BoardProfile,string,string[],Task<string>>? testTool;
    private readonly string? testBackups;
    public Installer(IProgress<string> progress) => this.progress = progress;
    internal Installer(IProgress<string> progress, Func<BoardProfile> profile, Func<BoardProfile,string,string[],Task<string>> tool, string backups) {
        this.progress=progress;testProfile=profile;testTool=tool;testBackups=backups;
    }
    public static string[] Ports()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        return key?.GetValueNames().Select(n => key.GetValue(n)?.ToString() ?? "").Where(n => Regex.IsMatch(n, "^COM[0-9]+$")).Distinct().Order().ToArray() ?? [];
    }
    public static BoardProfile PreparePayload()=>PreparePayload(BoardCatalog.DefaultId);
    public static BoardProfile PreparePayload(string boardId)
    {
        var definition=BoardCatalog.Get(boardId);
        // Extraction is into our versioned directory; no user inputs become archive paths.
        Directory.CreateDirectory(PayloadPath);
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream("ClawPal.Payload") ?? throw new IOException(L.Text("this_development_build_has_no_firmware_bundle_run_scripts"));
        using var zip = new ZipArchive(stream);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(PayloadPath, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(PayloadPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.Text("invalid_bundle_path"));
            if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true);
        }
        var board = JsonSerializer.Deserialize<BoardProfile>(File.ReadAllText(Path.Combine(PayloadPath, "boards", boardId+".json"))) ?? throw new IOException(L.Text("missing_board_profile"));
        if(board.Id!=definition.Id||board.Chip!=definition.Chip||board.Width!=definition.Width||board.Height!=definition.Height||board.FlashBytes!=definition.FlashBytes)throw new IOException(L.Text("this_backup_does_not_match_the_selected_board"));
        CheckHash(Path.Combine(PayloadPath, "tools", "esptool.exe"), board.ToolSha256);
        CheckHash(Path.Combine(PayloadPath,"tools","PresentMon","PresentMon.exe"),
            "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af");
        foreach (var part in board.Parts) CheckHash(Path.Combine(PayloadPath, part.File), part.Sha256);
        return board;
    }
    public static void CheckHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.Text("integrity_check_failed") + Path.GetFileName(path));
    }
    private async Task<string> Tool(BoardProfile board, string port, params string[] args)
    {
        if(testTool is not null)return await testTool(board,port,args);
        var start = new ProcessStartInfo(Path.Combine(PayloadPath, "tools", "esptool.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "--chip", board.Chip, "--port", port, "--baud", "460800", "--connect-attempts", "2" }.Concat(args)) start.ArgumentList.Add(a);
        string operation = args.First(a => a is "read_flash" or "verify_flash" or "write_flash" or "flash_id");
        bool reading = operation == "read_flash";
        long total = reading ? long.Parse(args[Array.IndexOf(args, "read_flash") + 2], CultureInfo.InvariantCulture) : 0;
        var watch = Stopwatch.StartNew();
        Log.Write($"USB {operation} started on {port}.");
        using var process = Process.Start(start) ?? throw new IOException(L.Text("the_flashing_tool_could_not_start"));
        var output = new StringBuilder();
        long lastOutput = Environment.TickCount64, lastReport = 0;
        int loggedPercent = -1;
        string latest = operation switch { "read_flash" => L.Text("starting_backup"), "write_flash" => L.Text("installing_firmware"), "verify_flash" => L.Text("verifying_flash"), _ => L.Text("checking_usb_device") };
        var gate = new object();
        void Record(string line)
        {
            lock (gate)
            {
                output.AppendLine(line);
                if (output.Length > 64000) output.Remove(0, output.Length - 48000);
                string? detail = reading ? UsbToolOutput.ReadProgress(line, total, watch.Elapsed) : null;
                int? percent = UsbToolOutput.Percent(line);
                if (percent is not null && percent.Value / 10 > loggedPercent) {
                    loggedPercent = percent.Value / 10;
                    Log.Write($"USB {operation} {percent}% after {watch.Elapsed.TotalSeconds:0}s.");
                }
                if (detail is not null) latest = detail;
                else if (percent is not null) latest = (operation == "write_flash" ? L.Text("installing") : L.Text("verifying")) + line;
                if (Environment.TickCount64 - lastReport >= 250 || percent == 100) {
                    lastReport = Environment.TickCount64; progress.Report(latest);
                }
            }
        }
        async Task Read(StreamReader reader)
        {
            var decoder = new UsbToolOutput(Record);
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0) {
                Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
                decoder.Feed(buffer.AsSpan(0, count));
            }
            decoder.Flush();
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        var exit = process.WaitForExitAsync();
        string? failure = null;
        while (!exit.IsCompleted)
        {
            await Task.WhenAny(exit, Task.Delay(1000));
            if (exit.IsCompleted) break;
            if (watch.Elapsed > (operation == "flash_id" ? TimeSpan.FromSeconds(25) : TimeSpan.FromMinutes(20))) {
                failure = L.Text("the_usb_operation_timed_out_close_serial_monitors_reconnect"); break;
            }
            if (reading && Environment.TickCount64 - Interlocked.Read(ref lastOutput) > 60000) {
                failure = L.Text("the_backup_stopped_sending_data_for_a_minute_nothing"); break;
            }
            lock (gate) {
                if (Environment.TickCount64 - lastReport >= 1000) {
                    lastReport = Environment.TickCount64;
                    progress.Report(latest.Contains('%') ? latest : $"{latest} · {watch.Elapsed:mm\\:ss}");
                }
            }
        }
        if (failure is not null) { try { process.Kill(true); } catch (InvalidOperationException) { } }
        await exit;
        await Task.WhenAll(stdout, stderr);
        string result = output.ToString();
        if (failure is not null) { Log.Write($"USB {operation}: {failure}"); throw new IOException(failure); }
        Log.Write($"USB {operation} finished in {watch.Elapsed.TotalSeconds:0.0}s, exit {process.ExitCode}.");
        if (process.ExitCode != 0) throw new IOException(L.Text("usb_operation_failed_close_serial_monitors_and_check_the") + result[^Math.Min(result.Length, 1600)..]);
        return result;
    }
    public async Task<DetectedDevice> Detect(string port, string boardId = BoardCatalog.DefaultId)
    {
        if (!Regex.IsMatch(port, "^COM[0-9]+$")) throw new ArgumentException(L.Text("choose_a_connected_usb_device"));
        progress.Report(L.Text("checking_the_usb_device_the_display_may_briefly_restart"));
        var board = await Task.Run(testProfile ?? (()=>PreparePayload(boardId)));
        string id = await Identify(board, port);
        Log.Write($"USB confirmed ESP32-S3, {board.FlashBytes / 1048576} MB, {port}, device {id}.");
        return new(port, id, board.FlashBytes);
    }
    private async Task<string> Identify(BoardProfile board, string port)
    {
        progress.Report(L.Text("checking_the_connected_esp32_s3"));
        string output = await Tool(board, port, "flash_id");
        return ParseIdentity(board, output);
    }
    internal static string ParseIdentity(BoardProfile board, string output)
    {
        if (!Regex.IsMatch(output, @"Chip is ESP32-S3\b", RegexOptions.IgnoreCase))
            throw new IOException(L.Text("this_usb_device_is_not_a_supported_esp32_s3"));
        var flash = Regex.Match(output, @"Detected flash size:\s*(\d+)(KB|MB)", RegexOptions.IgnoreCase);
        long detected = flash.Success ? long.Parse(flash.Groups[1].Value) * (flash.Groups[2].Value.Equals("MB",StringComparison.OrdinalIgnoreCase) ? 1048576 : 1024) : 0;
        if (detected != board.FlashBytes) throw new IOException(L.Text("this_board_profile_requires_0_mb_flash_no_firmware", board.FlashBytes / 1048576));
        var match = Regex.Match(output, @"MAC:\s*([0-9a-f:]{17})", RegexOptions.IgnoreCase);
        if (!match.Success) throw new IOException(L.Text("could_not_identify_the_device_mac_address_no_firmware"));
        return match.Groups[1].Value.Replace(":", "").ToLowerInvariant();
    }
    public async Task<string?> Install(SetupOptions options, Configuration config, Action pairingReady)
    {
        options.Validate();
        config.CompanionLanguage=options.CompanionLanguage;
        progress.Report(L.Text("checking_the_installation_bundle"));
        var board = await Task.Run(testProfile ?? (()=>PreparePayload(options.BoardId)));
        var id = await Identify(board, options.Port);
        string? folder = await BackupIfRequested(board, options, id);
        if (folder is not null && File.Exists(Configuration.ConfigPath))
            File.Copy(Configuration.ConfigPath, Path.Combine(folder, "companion-config.json"));
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string setupKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var values = new Dictionary<string, string> { ["ssid"] = options.Ssid, ["password"] = options.Password, ["token"] = token, ["setup_key"] = setupKey, ["city"] = options.City, ["latitude"] = options.Latitude.ToString("F6", CultureInfo.InvariantCulture), ["longitude"] = options.Longitude.ToString("F6", CultureInfo.InvariantCulture), ["timezone"] = options.Timezone, ["language"] = options.DisplayLanguage };
        string nvs = Path.Combine(Configuration.DirectoryPath, "provision-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(nvs, NvsImage.Create(values));
            // Persist credentials before writing, so loss of power cannot orphan the new device.
            config.SetupComplete=false; config.BoardId=board.Id; config.PairValue(token, id); ApplyPreferences(config, options); config.Save();
            await InstallCompanion(options.StartWithWindows);
            progress.Report(L.Text("installing_clawpal_keep_usb_connected_until_this_finishes"));
            var arguments = new List<string> { "--before", "default_reset", "write_flash", "--flash_mode", "keep", "--flash_freq", "keep", "--flash_size", "keep" };
            foreach (var part in board.Parts) { arguments.Add(part.Offset); arguments.Add(Path.Combine(PayloadPath, part.File)); }
            arguments.Add("0x9000"); arguments.Add(nvs);
            await Tool(board, options.Port, arguments.ToArray());
            progress.Report(L.Text("checking_the_installed_firmware"));
            var verify = new List<string> { "verify_flash" };
            foreach (var part in board.Parts) { verify.Add(part.Offset); verify.Add(Path.Combine(PayloadPath, part.File)); }
            // NVS is excluded here: first boot can legitimately change its contents.
            await Tool(board, options.Port, verify.ToArray());
            config.SetupComplete=true; config.DisplaySettings=DeviceSettings.FromConfiguration(config) with {Language=options.DisplayLanguage}; config.SettingsPending=false; config.Save();
            pairingReady();
            progress.Report(L.Text("firmware_installed_waiting_for_clawpal_on_wi_fi"));
            return folder;
        }
        finally { if (File.Exists(nvs)) File.Delete(nvs); }
    }
    internal async Task<string?> BackupIfRequested(BoardProfile board, SetupOptions options, string id)
    {
        if (!options.CreateBackup) { progress.Report(L.Text("backup_skipped_as_selected_preparing_installation")); return null; }
        var folder = Path.Combine(testBackups ?? BackupsPath, id, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, "flash.bin");
        progress.Report(L.Text("reading_the_full_backup_usually_5_8_minutes_on"));
        await Tool(board, options.Port, "--after", "no_reset", "read_flash", "0", board.FlashBytes.ToString(), backup);
        if (new FileInfo(backup).Length != board.FlashBytes) throw new IOException(L.Text("the_backup_is_incomplete_installation_stopped_before_writing"));
        progress.Report(L.Text("verifying_the_backup_against_device_flash"));
        await Tool(board, options.Port, "--before", "no_reset", "--after", "no_reset", "verify_flash", "0", backup);
        using (var stream = File.OpenRead(backup)) {
            var record = new BackupRecord(board.Id, id, stream.Length, Convert.ToHexString(SHA256.HashData(stream)), DateTimeOffset.Now);
            await File.WriteAllTextAsync(Path.Combine(folder, "backup.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        return folder;
    }
    public static void ApplyPreferences(Configuration config, SetupOptions options)
    {
        config.City = options.City; config.Latitude = options.Latitude; config.Longitude = options.Longitude;
        config.Timezone = options.Timezone; config.TrackFps = options.TrackFps; config.AutoOpenApps = options.AutoOpen;
    }
    public async Task EnableWirelessUpdates(string port, Configuration config)
    {
        if(config.BoardId!=BoardCatalog.DefaultId)throw new IOException(L.Text("usb_migration_original_only"));
        var board=await Task.Run(()=>PreparePayload(config.BoardId));
        if(!config.SetupComplete || config.Token.Length!=64)throw new IOException(L.Text("pair_the_display_before_preparing_wireless_updates"));
        if(await Identify(board,port)!=config.DeviceId)throw new IOException(L.Text("this_usb_display_is_not_the_paired_device_nothing"));
        // Preserve the complete boot/table/settings prefix before the additive layout migration.
        var folder=Path.Combine(BackupsPath,config.DeviceId,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-ota-migration");Directory.CreateDirectory(folder);
        var prefix=Path.Combine(folder,"boot-and-settings.bin");
        progress.Report(L.Text("saving_the_current_boot_and_settings_partitions"));
        await Tool(board,port,"--after","no_reset","read_flash","0","65536",prefix);
        if(new FileInfo(prefix).Length!=0x10000)throw new IOException(L.Text("settings_backup_was_incomplete_update_stopped"));
        await Tool(board,port,"--before","no_reset","--after","no_reset","verify_flash","0",prefix);
        File.Copy(Configuration.ConfigPath,Path.Combine(folder,"companion-config.json"));
        await File.WriteAllTextAsync(Path.Combine(folder,"migration.json"),JsonSerializer.Serialize(new {board=board.Id,device=config.DeviceId,offset=0,bytes=0x10000,sha256=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(prefix)))}));
        progress.Report(L.Text("preparing_wi_fi_update_slots_keep_usb_connected"));
        var arguments=new List<string>{"--before","no_reset","--after","no_reset","write_flash","--flash_mode","keep","--flash_freq","keep","--flash_size","keep"};
        foreach(var part in board.Parts){if(part.Offset=="0x9000")throw new IOException(L.Text("update_bundle_must_not_overwrite_settings"));arguments.Add(part.Offset);arguments.Add(Path.Combine(PayloadPath,part.File));}
        await Tool(board,port,arguments.ToArray());
        var verify=new List<string>{"--before","no_reset","verify_flash"};foreach(var part in board.Parts){verify.Add(part.Offset);verify.Add(Path.Combine(PayloadPath,part.File));}
        await Tool(board,port,verify.ToArray());
        progress.Report(L.Text("wi_fi_updates_enabled_pairing_and_settings_preserved"));
    }
    // Provisioning images hold the Wi-Fi password and pairing token; remove any left behind by an interrupted installation.
    public static void DeleteStaleProvisioningImages()
    {
        if (!Directory.Exists(Configuration.DirectoryPath)) return;
        foreach (var file in Directory.GetFiles(Configuration.DirectoryPath, "provision-*.bin"))
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public async Task Restore(string port, string metadataPath, string boardId = BoardCatalog.DefaultId)
    {
        var board = PreparePayload(boardId);
        var record = JsonSerializer.Deserialize<BackupRecord>(await File.ReadAllTextAsync(metadataPath)) ?? throw new IOException(L.Text("invalid_backup_metadata"));
        if (record.BoardId != board.Id || record.Bytes != board.FlashBytes) throw new IOException(L.Text("this_backup_does_not_match_the_selected_board"));
        string backup = Path.Combine(Path.GetDirectoryName(metadataPath)!, "flash.bin");
        if (new FileInfo(backup).Length != board.FlashBytes) throw new IOException(L.Text("the_backup_has_the_wrong_size"));
        CheckHash(backup, record.Sha256);
        if (await Identify(board, port) != record.DeviceId) throw new IOException(L.Text("this_backup_belongs_to_a_different_device_restore_stopped"));
        progress.Report(L.Text("restoring_your_saved_firmware_keep_usb_connected"));
        // Disable boot after writing until verification completes; the original app may modify NVS.
        await Tool(board, port, "--after", "no_reset", "write_flash", "0", backup);
        await Tool(board, port, "--before", "no_reset", "verify_flash", "0", backup);
        var oldConfig = Path.Combine(Path.GetDirectoryName(metadataPath)!, "companion-config.json");
        if (File.Exists(oldConfig)) File.Copy(oldConfig, Configuration.ConfigPath, true);
        progress.Report(L.Text("backup_restored_restart_the_companion_to_reload_pairing"));
    }
    private static Task InstallCompanion(bool startup)
    {
        string destination = Path.GetDirectoryName(Startup.InstalledPath)!;
        Directory.CreateDirectory(destination);
        if (!StartupContract.SamePath(Environment.ProcessPath, Startup.InstalledPath))
            File.Copy(Environment.ProcessPath!, Startup.InstalledPath, true);
        string sourceTools = Path.Combine(AppContext.BaseDirectory, "Tools");
        if (Directory.Exists(sourceTools)) foreach (string source in Directory.GetFiles(sourceTools, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(destination, "Tools", Path.GetRelativePath(sourceTools, source));
            if (StartupContract.SamePath(source, target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
        }
        Startup.CreateStartMenuShortcut(); Startup.Set(startup);
        // The installed-apps entry is a convenience; failing to write it must not fail setup.
        try { Uninstaller.Register(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Write("Uninstall entry unavailable: " + error.GetType().Name); }
        return Task.CompletedTask;
    }
}

