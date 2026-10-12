using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace ClawPal.Companion;

internal sealed record UsbDevice(string Port, string Name, string HardwareId)
{
    public bool IsEspressif => HardwareId.Contains("VID_303A", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Name}  ·  {Port}";
}

internal static class DeviceDiscovery
{
    // SERIALCOMM contains present ports; Enum supplies friendly USB names, including
    // localized Windows names. Never infer the screen model from a USB vendor ID.
    internal static UsbDevice[] Find()
    {
        var present = Installer.Ports().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, UsbDevice>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return [];
        foreach (string hardware in usb.GetSubKeyNames())
        {
            using var model = usb.OpenSubKey(hardware);
            if (model is null) continue;
            foreach (string instance in model.GetSubKeyNames())
            {
                using var device = model.OpenSubKey(instance);
                using var parameters = device?.OpenSubKey("Device Parameters");
                if (parameters?.GetValue("PortName") is not string port || !present.Contains(port)) continue;
                string name = device?.GetValue("FriendlyName") as string ?? device?.GetValue("DeviceDesc") as string ?? L.Text("usb_serial_device");
                name = Regex.Replace(name.Split(';')[^1], @"\s*\(COM\d+\)\s*$", "").Trim();
                if (hardware.Contains("VID_303A", StringComparison.OrdinalIgnoreCase)) name = L.Text("espressif_usb_device");
                devices[port] = new(port, name, hardware + "\\" + instance);
            }
        }
        return devices.Values.OrderByDescending(d => d.IsEspressif).ThenBy(d => int.Parse(d.Port[3..])).ToArray();
    }

    internal static UsbDevice? Preferred(IReadOnlyList<UsbDevice> devices, UsbDevice? previous)
    {
        if (previous is not null && devices.FirstOrDefault(d => d == previous) is { } existing) return existing;
        var candidates = devices.Where(d => d.IsEspressif).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}

public sealed record DetectedDevice(string Port, string DeviceId, int FlashBytes)
{
    public string Summary => L.Text("esp32_s3_confirmed_0_mb_flash_1", FlashBytes / 1048576, Port);
}
