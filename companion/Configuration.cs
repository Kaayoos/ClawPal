using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClawPal.Companion;

public sealed class Configuration
{
    public static string DirectoryPath => BuddyPaths.DataDirectory(WindowsFolders.Profile);
    public static string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public string DeviceId { get; set; } = "";
    public string BoardId { get; set; } = BoardCatalog.DefaultId;
    public string ProtectedToken { get; set; } = "";
    public string? LastAddress { get; set; }
    public bool Paused { get; set; }
    public bool TrackFps { get; set; }
    public string City { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Timezone { get; set; } = "UTC0";
    public bool AutoOpenApps { get; set; }
    public string UpdateNotificationKey { get; set; } = "";
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string CompanionLanguage { get; set; } = "en";
    public bool SetupComplete { get; set; }
    public DeviceSettings? DisplaySettings { get; set; }
    public bool SettingsPending { get; set; }
    public void SetDisplaySettings(DeviceSettings settings) {
        settings.Validate(); lock(gate) { DisplaySettings=settings;SettingsPending=true;CopyLocation(settings);Save(); }
    }
    public void RememberDisplaySettings(DeviceSettings settings) {
        settings.Validate();lock(gate) { if(SettingsPending)return;DisplaySettings=settings;CopyLocation(settings);Save(); }
    }
    public bool ConfirmDisplaySettings(DeviceSettings settings) {
        lock(gate) { if(!ReferenceEquals(DisplaySettings,settings))return false;SettingsPending=false;Save();return true; }
    }
    private void CopyLocation(DeviceSettings settings) { City=settings.City;Latitude=settings.Latitude;Longitude=settings.Longitude;Timezone=settings.Timezone; }
    [JsonIgnore] public string Token { get; private set; } = "";

    public static Configuration Load()
    {
        var config = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConfigPath)) ?? new() : new();
        if (config.ProtectedToken.Length > 0) config.Token = SecretProtection.Unprotect(config.ProtectedToken);
        if(File.Exists(ConfigPath)) {
            using var saved=JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if(!saved.RootElement.TryGetProperty(nameof(SetupComplete),out _) && config.Token.Length==64) config.SetupComplete=true;
        }
        config.DisplaySettings = (config.DisplaySettings ?? DeviceSettings.FromConfiguration(config)).Upgrade();
        config.CompanionLanguage=L.Normalize(config.CompanionLanguage);
        config.DisplaySettings=config.DisplaySettings with {Language=L.Normalize(config.DisplaySettings.Language)};
        return config;
    }

    public void Pair(string tokenFile, string deviceId) => PairValue(File.ReadAllText(tokenFile).Trim(), deviceId);
    public void PairValue(string token, string deviceId)
    {
        if (!Protocol.IsLowerHex(token, 64)) throw new ArgumentException(L.Text("the_pairing_token_must_be_64_lowercase_hexadecimal_characters"));
        if (!Protocol.IsLowerHex(deviceId, 12)) throw new ArgumentException(L.Text("the_device_id_must_be_12_lowercase_hexadecimal_characters"));
        Token = token;
        ProtectedToken = SecretProtection.Protect(token);
        DeviceId = deviceId;
        LastAddress = null;
        Save();
    }

    private readonly object gate = new();
    public void Save()
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporary = ConfigPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ConfigPath, true);
        }
    }
}

internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static string Protect(string text) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));
    public static string Unprotect(string text) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text), false));
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "ClawPal pairing", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}

public static class Startup
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string InstalledPath => BuddyPaths.InstalledExecutable(WindowsFolders.Profile);
    public static string ShortcutPath => Path.Combine(WindowsFolders.Startup, "ClawPal.lnk");
    public static string StartMenuPath => Path.Combine(WindowsFolders.Programs, "ClawPal.lnk");
    public static bool Enabled
    {
        get
        {
            if (!File.Exists(InstalledPath) || !File.Exists(ShortcutPath)) return false;
            object? shell = null, shortcut = null;
            try
            {
                shell = CreateShell();
                shortcut = ((dynamic)shell).CreateShortcut(ShortcutPath);
                return StartupContract.ValidStartupLink((string)((dynamic)shortcut).TargetPath, (string)((dynamic)shortcut).Arguments, InstalledPath, true);
            }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
            finally { Release(shortcut); Release(shell); }
        }
    }
    public static void Set(bool enabled)
    {
        // Save the replacement before removing our legacy Run entry. Do not touch
        // Windows StartupApproved settings or other applications' registration.
        if (enabled) CreateShortcut(ShortcutPath, StartupContract.StartupArgument);
        else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        key?.DeleteValue("ClawPal", false);
        Log.Write(enabled ? "Windows startup shortcut enabled." : "Windows startup shortcut disabled.");
    }
    public static void CreateStartMenuShortcut() => CreateShortcut(StartMenuPath, "");

    private static object CreateShell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)
        ?? throw new COMException(L.Text("windows_shortcut_service_unavailable"));
    private static void CreateShortcut(string path, string arguments)
    {
        if (!File.Exists(InstalledPath)) throw new FileNotFoundException(L.Text("install_clawpal_before_creating_its_shortcuts"), InstalledPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object? shell = null, shortcut = null;
        try
        {
            // WScript.Shell shortcut properties are documented by Microsoft:
            // learn.microsoft.com/troubleshoot/windows-client/admin-development/create-desktop-shortcut-with-wsh
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = InstalledPath;
            link.WorkingDirectory = Path.GetDirectoryName(InstalledPath)!;
            link.Arguments = arguments;
            link.WindowStyle = 7;
            link.Description = L.Text("clawpal_tray_companion");
            link.IconLocation = InstalledPath + ",0";
            link.Save();
            if (!File.Exists(path)) throw new IOException(L.Text("windows_did_not_create_the_clawpal_shortcut"));
        }
        finally { Release(shortcut); Release(shell); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

public static class Log
{
    private static readonly object Gate = new();
    public static string PathName => Path.Combine(Configuration.DirectoryPath, "companion.log");
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Configuration.DirectoryPath);
                if (File.Exists(PathName) && new FileInfo(PathName).Length > 1_000_000) File.Move(PathName, PathName + ".old", true);
                File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
