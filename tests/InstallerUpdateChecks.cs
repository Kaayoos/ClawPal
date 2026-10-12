using ClawPal.Companion;
using System.Text.Json;

internal static class InstallerUpdateChecks
{
    internal static async Task<int> Run(string directory, SetupOptions options, BoardProfile profile)
    {
        int count = 0;
        void Check(bool good, string name) { if (!good) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        Check(options.CreateBackup, "backup enabled by default");
        var records = new List<string>();
        var decoder = new UsbToolOutput(records.Add);
        decoder.Feed("Starting\r\n4096 (0 %)");
        Check(records.SequenceEqual(new[] { "Starting" }), "partial terminal record waits for delimiter");
        decoder.Feed("\b\b\b8192 (1 %)\b\b");
        Check(records.SequenceEqual(new[] { "Starting", "4096 (0 %)", "8192 (1 %)" }), "backspace progress arrives before newline");
        decoder.Feed("16384 (2"); decoder.Feed(" %)\rFinished\n"); decoder.Flush();
        Check(records[^2] == "16384 (2 %)" && records[^1] == "Finished", "fragmented progress and mixed delimiters decoded");
        Check(UsbToolOutput.Percent("Writing ( 47 % )") == 47, "write progress parsed");
        Check(UsbToolOutput.Percent("101%") is null && UsbToolOutput.Percent("16 MB") is null, "invalid percentage rejected");
        var report = UsbToolOutput.ReadProgress("8388608 (50 %)", 16777216, TimeSpan.FromMinutes(3));
        Check(report is not null && report.Contains("50%") && report.Contains("03:00") && report.Contains("3 min left"), "backup percentage elapsed time and ETA");
        Check(UsbToolOutput.ReadProgress("16800000 (100 %)", 16777216, TimeSpan.Zero) is null, "impossible backup byte count rejected");
        var fragmented = new List<string>(); var byteDecoder = new UsbToolOutput(fragmented.Add);
        foreach (char c in "10 (1 %)\b\b20 (2 %)\n") byteDecoder.Feed(c.ToString());
        Check(fragmented.SequenceEqual(new[] { "10 (1 %)", "20 (2 %)" }), "single-character pipe chunks supported");

        var esp = new UsbDevice("COM4", "Espressif USB device", "USB\\VID_303A&PID_1001\\one");
        var generic = new UsbDevice("COM8", "USB serial device", "USB\\VID_1A86&PID_7523\\two");
        var secondEsp = esp with { Port = "COM9", HardwareId = "USB\\VID_303A&PID_1001\\three" };
        Check(DeviceDiscovery.Preferred([generic, esp], null) == esp, "single Espressif device selected among other USB devices");
        Check(DeviceDiscovery.Preferred([esp, secondEsp], null) is null, "multiple Espressif devices require selection");
        Check(DeviceDiscovery.Preferred([generic], null) is null, "generic USB bridge is not assumed to be ESP32");
        Check(DeviceDiscovery.Preferred([esp, generic], generic) == generic, "explicit existing selection is preserved");
        Check(DeviceDiscovery.Preferred([], esp) is null, "unplugged device is not kept");
        Check(DeviceDiscovery.Preferred([generic with { Port = "COM4" }], esp) is null, "reused COM number does not preserve wrong identity");
        string transcript = "Chip is ESP32-S3 (QFN56) (revision v0.2)\nMAC: 02:11:22:33:44:55\nDetected flash size: 16MB\n";
        Check(Installer.ParseIdentity(profile, transcript) == "021122334455", "realistic ESP32-S3 identification parsed");
        foreach (string wrong in new[] { transcript.Replace("ESP32-S3", "ESP32-C3"), transcript.Replace("16MB", "8MB"), transcript.Replace("MAC:", "No address:") }) {
            bool rejected = false;
            try { Installer.ParseIdentity(profile, wrong); } catch (IOException) { rejected = true; }
            Check(rejected, "incompatible or incomplete device check rejected");
        }

        var calls = new List<string[]>();
        bool failVerification = false;
        Task<string> Tool(BoardProfile board, string port, string[] args) {
            calls.Add(args);
            if (args.Contains("read_flash")) { using var file = File.Create(args[^1]); file.SetLength(board.FlashBytes); }
            else if (args.Contains("verify_flash")) { if (failVerification) throw new IOException("Verification failed"); }
            else throw new Exception("Backup test attempted unexpected operation");
            return Task.FromResult("");
        }
        var backups = Path.Combine(directory, "optional-backup-" + Guid.NewGuid().ToString("N"));
        var installer = new Installer(new Progress<string>(), () => profile, Tool, backups);
        string? skipped = await installer.BackupIfRequested(profile, options with { CreateBackup = false }, "021122334455");
        Check(skipped is null && calls.Count == 0 && !Directory.Exists(backups), "disabled backup does not read verify or create a folder");
        string? saved = await installer.BackupIfRequested(profile, options, "021122334455");
        Check(saved is not null && calls.Count == 2 && calls[0].Contains("read_flash") && calls[1].Contains("verify_flash"), "enabled backup reads then verifies");
        var metadata = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(Path.Combine(saved!, "backup.json")))!;
        Installer.CheckHash(Path.Combine(saved!, "flash.bin"), metadata.Sha256);
        Check(metadata.Bytes == profile.FlashBytes && metadata.DeviceId == "021122334455", "successful backup has valid restorable metadata");
        failVerification = true;
        bool failed = false;
        try { await installer.BackupIfRequested(profile, options, "021122334455"); } catch (IOException) { failed = true; }
        Check(failed && Directory.GetFiles(backups, "backup.json", SearchOption.AllDirectories).Length == 1, "failed verification never creates restorable metadata");
        Check(!calls.Any(c => c.Contains("write_flash")), "backup checks never write device flash");
        return count;
    }
}
