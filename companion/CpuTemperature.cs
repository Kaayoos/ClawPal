using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawPal.Companion;

public static class SensorContract
{
    // Package or control temperature first; per-core and board sensors only as a fallback.
    private static readonly string[] Preferred =
        ["CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Core (Tdie)", "CPU Die (average)", "Core (Tctl)", "CPU (Tctl)", "Core Max", "Core Average", "CPU"];

    public static double? PickCpu(IEnumerable<(string Name, double Celsius)> readings)
    {
        var valid = readings.Where(r => double.IsFinite(r.Celsius) && r.Celsius > 0 && r.Celsius < 125).ToArray();
        foreach (var name in Preferred)
            foreach (var reading in valid)
                if (reading.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return Math.Round(reading.Celsius, 1);
        return null;
    }
}

// Optional CPU temperature from a monitoring app the user already runs. Windows has no
// standard CPU package sensor and ClawPal installs no kernel driver, so it only reads
// what LibreHardwareMonitor (WMI) or HWiNFO (shared memory) already publish.
internal sealed class CpuTemperature
{
    private DateTime next;
    private double? value;
    private string source = "";
    private object? wmi;
    private string? wmiName;
    public string Status => source.Length > 0 ? "CPU temperature from " + source + "." :
        "Unavailable: run LibreHardwareMonitor, or HWiNFO with shared memory support, to show CPU temperature. ClawPal installs no sensor driver.";

    public double? Read()
    {
        var now = DateTime.UtcNow;
        if (now < next) return value;
        value = HwInfo() ?? HardwareMonitor();
        if (value is null) source = "";
        // Look again soon while a source answers, and rarely while none is running.
        next = now.AddSeconds(value is null ? 30 : 2);
        return value;
    }

    // HWiNFO's documented shared-memory layout (Settings > Shared Memory Support).
    private double? HwInfo()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.Capacity < 44 || view.ReadUInt32(0) != 0x53695748 /* "HWiS"; "DEAD" once sharing stops */) return null;
            long offset = view.ReadUInt32(32), size = view.ReadUInt32(36), count = view.ReadUInt32(40);
            if (size < 292 || count > 8192 || offset + size * count > view.Capacity) return null;
            var readings = new List<(string, double)>();
            var text = new byte[128];
            for (long i = 0; i < count; ++i)
            {
                long at = offset + i * size;
                if (view.ReadUInt32(at) != 1 /* temperature */) continue;
                double reading = view.ReadDouble(at + 284);
                view.ReadArray(at + 268, text, 0, 16);
                if (Text(text, 16).Contains('F')) reading = (reading - 32) * 5 / 9;
                view.ReadArray(at + 12, text, 0, 128);
                readings.Add((Text(text, 128), reading));
            }
            var picked = SensorContract.PickCpu(readings);
            if (picked is not null) source = "HWiNFO";
            return picked;
        }
        catch (Exception error) when (error is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string Text(byte[] bytes, int length)
    {
        int end = Array.IndexOf(bytes, (byte)0, 0, length);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end);
    }

    // LibreHardwareMonitor and the older OpenHardwareMonitor publish sensors over WMI while running.
    private double? HardwareMonitor()
    {
        foreach (var name in wmiName is null ? new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" } : [wmiName])
        {
            try
            {
                if (wmi is null)
                {
                    dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
                    wmi = locator.ConnectServer(".", @"root\" + name);
                    wmiName = name;
                }
                dynamic service = wmi;
                var readings = new List<(string, double)>();
                foreach (dynamic sensor in service.ExecQuery("SELECT Name, Identifier, Value FROM Sensor WHERE SensorType='Temperature'"))
                {
                    string identifier = sensor.Identifier ?? "";
                    if (identifier.Contains("cpu", StringComparison.OrdinalIgnoreCase)) readings.Add(((string)(sensor.Name ?? ""), Convert.ToDouble(sensor.Value)));
                }
                var picked = SensorContract.PickCpu(readings);
                if (picked is not null) { source = name; return picked; }
            }
            catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or FormatException) { }
            if (wmi is not null && Marshal.IsComObject(wmi)) Marshal.ReleaseComObject(wmi);
            wmi = null; wmiName = null;
        }
        return null;
    }
}
