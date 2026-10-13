using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClawPal.Companion;

public sealed class Metrics : IDisposable
{
    private readonly GpuProvider gpu = new();
    private readonly string cpuName;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double previousTime;
    private ulong lastIdle, lastKernel, lastUser;
    private bool hasCpuSample;
    private readonly Dictionary<string, (long Rx, long Tx)> previousNetwork = new();
    public string GpuProviderName => gpu.ProviderName;
    public string CpuTemperatureStatus => "Unavailable: Windows exposes no standard CPU package temperature counter; no kernel sensor driver is installed by ClawPal.";

    public Metrics()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        cpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
    }

    public PcStats Sample(long seq)
    {
        double? usage = null;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (hasCpuSample) usage = CounterMath.CpuUsage(lastIdle, lastKernel, lastUser, idle, kernel, user);
            lastIdle = idle; lastKernel = kernel; lastUser = user; hasCpuSample = true;
        }
        var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        if (!Native.GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var totalMb = memory.TotalPhysical / 1048576d;
        var usedMb = (memory.TotalPhysical - memory.AvailablePhysical) / 1048576d;
        var now = clock.Elapsed.TotalSeconds;
        var elapsed = now - previousTime;
        double rx = 0, tx = 0;
        var current = new Dictionary<string, (long Rx, long Tx)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsMonitorAdapter(adapter)) continue;
            try
            {
                var stats = adapter.GetIPStatistics();
                current[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                if (previousNetwork.TryGetValue(adapter.Id, out var before))
                {
                    rx += CounterMath.BytesPerSecond(before.Rx, stats.BytesReceived, elapsed);
                    tx += CounterMath.BytesPerSecond(before.Tx, stats.BytesSent, elapsed);
                }
            }
            catch (NetworkInformationException) { }
        }
        previousNetwork.Clear();
        foreach (var item in current) previousNetwork.Add(item.Key, item.Value);
        previousTime = now;
        // The display rejects a whole frame when a name fills its 96-byte buffer.
        var gpuStats = gpu.Sample();
        return new("pc.stats", 1, seq, Environment.MachineName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new(usage, null, MusicContract.LimitText(cpuName, 95)), gpuStats with { Name = MusicContract.LimitText(gpuStats.Name, 95) },
            new(Math.Round(usedMb, 1), Math.Round(totalMb, 1), Math.Round(totalMb > 0 ? usedMb / totalMb * 100 : 0, 1)),
            new(Math.Round(rx, 1), Math.Round(tx, 1)), Environment.TickCount64 / 1000);
    }

    private static bool IsMonitorAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) return false;
        try { return adapter.GetIPProperties().GatewayAddresses.Count > 0; }
        catch (NetworkInformationException) { return false; }
    }
    public void Dispose() => gpu.Dispose();

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [StructLayout(LayoutKind.Sequential)] public struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}

internal sealed class GpuProvider : IDisposable
{
    private readonly Nvml? nvml;
    private readonly GpuCounters? counters;
    private readonly AmdTemperature? amd;
    public string ProviderName => nvml is not null ? "NVIDIA NVML" : counters is not null ? "Windows GPU performance counters + DXGI" + (amd is not null ? " + AMD PMLog temperature" : "") : "Unavailable";
    public GpuProvider()
    {
        nvml = Nvml.TryCreate();
        if (nvml is null)
        {
            var adapter = Dxgi.BestAdapter();
            counters = GpuCounters.TryCreate(adapter);
            amd = AmdTemperature.TryCreate(adapter);
        }
    }
    public GpuStats Sample()
    {
        var value = nvml?.Sample() ?? counters?.Sample() ?? new(null, null, null, null, "GPU unavailable");
        return amd is null ? value : value with { Temperature = amd.Sample() };
    }
    public void Dispose() { nvml?.Dispose(); counters?.Dispose(); amd?.Dispose(); }
}

internal sealed class Nvml : IDisposable
{
    private IntPtr library, device;
    private readonly Shutdown shutdown;
    private readonly Utilization utilization;
    private readonly Temperature temperature;
    private readonly Memory memory;
    private readonly string name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Device(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Name(IntPtr device, StringBuilder name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Utilization(IntPtr device, out UtilizationValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Temperature(IntPtr device, uint sensor, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Memory(IntPtr device, out MemoryValue value);
    [StructLayout(LayoutKind.Sequential)] private struct UtilizationValue { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryValue { public ulong Total, Free, Used; }
    private static T Function<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private Nvml(IntPtr library)
    {
        this.library = library;
        shutdown = Function<Shutdown>(library, "nvmlShutdown");
        if (Function<Init>(library, "nvmlInit_v2")() != 0) throw new InvalidOperationException("NVML initialization unavailable.");
        try
        {
            if (Function<Device>(library, "nvmlDeviceGetHandleByIndex_v2")(0, out device) != 0) throw new InvalidOperationException("No NVIDIA device.");
            utilization = Function<Utilization>(library, "nvmlDeviceGetUtilizationRates");
            temperature = Function<Temperature>(library, "nvmlDeviceGetTemperature");
            memory = Function<Memory>(library, "nvmlDeviceGetMemoryInfo");
            var buffer = new StringBuilder(128);
            name = Function<Name>(library, "nvmlDeviceGetName")(device, buffer, 128) == 0 ? buffer.ToString() : "NVIDIA GPU";
        }
        catch { shutdown(); throw; }
    }
    public static Nvml? TryCreate()
    {
        foreach (var path in new[] { Path.Combine(Environment.SystemDirectory, "nvml.dll"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll") })
        {
            if (!NativeLibrary.TryLoad(path, out var handle)) continue;
            try { return new Nvml(handle); }
            catch (Exception error) when (error is EntryPointNotFoundException or InvalidOperationException) { NativeLibrary.Free(handle); }
        }
        return null;
    }
    public GpuStats Sample()
    {
        double? used = null, total = null;
        if (memory(device, out var ram) == 0) { used = Math.Round(ram.Used / 1048576d, 1); total = Math.Round(ram.Total / 1048576d, 1); }
        return new(utilization(device, out var util) == 0 ? Math.Clamp(util.Gpu, 0u, 100u) : null,
            temperature(device, 0, out var temp) == 0 ? temp : null, used, total, name);
    }
    public void Dispose() { if (library == IntPtr.Zero) return; shutdown(); NativeLibrary.Free(library); library = IntPtr.Zero; }
}

internal sealed class GpuCounters : IDisposable
{
    private IntPtr query, usage, memory;
    private GpuAdapter? adapter;
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    public static GpuCounters? TryCreate(GpuAdapter? adapter)
    {
        var result = new GpuCounters { adapter = adapter };
        if (PdhOpenQuery(null, IntPtr.Zero, out result.query) != 0) return null;
        var usageResult = PdhAddEnglishCounter(result.query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out result.usage);
        var memoryResult = PdhAddEnglishCounter(result.query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out result.memory);
        if (usageResult != 0 && memoryResult != 0) { result.Dispose(); return null; }
        PdhCollectQueryData(result.query);
        return result;
    }
    public GpuStats Sample()
    {
        PdhCollectQueryData(query);
        var engines = Read(usage).Where(MatchesAdapter).ToList();
        var allocations = Read(memory).Where(MatchesAdapter).ToList();
        // WDDM publishes one instance per process and engine. Aggregate each physical engine,
        // then use its busiest engine; summing unrelated engines can exceed 100%.
        var grouped = engines.GroupBy(x => x.Name.Contains("_luid_") ? x.Name[x.Name.IndexOf("_luid_", StringComparison.Ordinal)..] : x.Name);
        double? busy = engines.Count == 0 ? null : Math.Round(Math.Clamp(grouped.Max(g => g.Sum(v => v.Value)), 0, 100), 1);
        double? used = allocations.Count == 0 ? null : Math.Round(allocations.Sum(x => x.Value) / 1048576d, 1);
        return new(busy, null, used, adapter?.DedicatedMb, adapter?.Name ?? "Windows GPU");
    }
    private bool MatchesAdapter((string Name, double Value) value) => adapter is null || value.Name.Contains(adapter.CounterLuid, StringComparison.OrdinalIgnoreCase);
    private static List<(string Name, double Value)> Read(IntPtr counter)
    {
        var values = new List<(string, double)>();
        if (counter == IntPtr.Zero) return values;
        uint bytes = 0;
        if (PdhGetFormattedCounterArray(counter, 0x00000200, ref bytes, out _, IntPtr.Zero) != 0x800007D2 || bytes > 8_000_000) return values;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x00000200, ref bytes, out var count, buffer) != 0) return values;
            int size = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                if (item.Value.Status is 0 or 1 && double.IsFinite(item.Value.Value)) values.Add((Marshal.PtrToStringUni(item.Name) ?? "", Math.Max(0, item.Value.Value)));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return values;
    }
    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
}
