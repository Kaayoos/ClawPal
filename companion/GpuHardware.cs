using System.Runtime.InteropServices;

namespace ClawPal.Companion;

internal sealed record GpuAdapter(string Name, double DedicatedMb, string CounterLuid, uint VendorId);

internal static class Dxgi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self, out Description description);
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    public static GpuAdapter? BestAdapter()
    {
        var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref factoryId, out var factory) < 0) return null;
        try
        {
            GpuAdapter? best = null;
            for (uint index = 0; index < 32; index++)
            {
                if (Method<EnumAdapters>(factory, 12)(factory, index, out var adapter) < 0) break;
                try
                {
                    if (Method<GetDescription>(adapter, 10)(adapter, out var desc) < 0 || (desc.Flags & 2) != 0) continue;
                    var candidate = new GpuAdapter(desc.Name.Trim(), desc.DedicatedVideoMemory.ToUInt64() / 1048576d, $"luid_0x{unchecked((uint)desc.LuidHigh):x8}_0x{desc.LuidLow:x8}", desc.VendorId);
                    if (best is null || candidate.DedicatedMb > best.DedicatedMb) best = candidate;
                }
                finally { Marshal.Release(adapter); }
            }
            return best;
        }
        finally { Marshal.Release(factory); }
    }
}

// Uses read-only PMLog data from the AMD driver already installed on the computer.
// No tuning API, driver installation, or administrator privileges are involved.
internal sealed class AmdTemperature : IDisposable
{
    private IntPtr library, context;
    private readonly Allocate allocate = Marshal.AllocHGlobal;
    private Destroy? destroy;
    private Query? query;
    private int adapterIndex;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr Allocate(int bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(Allocate allocate, int connectedOnly, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Destroy(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(IntPtr context, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetInfo(IntPtr context, IntPtr adapters, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Query(IntPtr context, int adapterIndex, ref LogData data);
    [StructLayout(LayoutKind.Sequential)] private struct Sensor { public int Supported, Value; }
    [StructLayout(LayoutKind.Sequential)] private struct LogData { public int Size; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public Sensor[] Sensors; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] private struct AdapterInfo
    {
        public int Size, Index;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
        public int Bus, Device, Function, Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present, Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Pnp;
        public int OsDisplayIndex;
    }
    private T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public static AmdTemperature? TryCreate(GpuAdapter? adapter)
    {
        if (adapter?.VendorId != 0x1002) return null;
        var result = new AmdTemperature();
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"), out result.library)) return null;
        try
        {
            result.destroy = result.Function<Destroy>("ADL2_Main_Control_Destroy");
            if (result.Function<Create>("ADL2_Main_Control_Create")(result.allocate, 1, out result.context) != 0) throw new InvalidOperationException();
            result.query = result.Function<Query>("ADL2_New_QueryPMLogData_Get");
            if (result.Function<Count>("ADL2_Adapter_NumberOfAdapters_Get")(result.context, out var count) != 0 || count < 1 || count > 64) throw new InvalidOperationException();
            var size = Marshal.SizeOf<AdapterInfo>();
            var buffer = Marshal.AllocHGlobal(size * count);
            try
            {
                for (int i = 0; i < count; i++) Marshal.StructureToPtr(new AdapterInfo { Size = size }, buffer + i * size, false);
                if (result.Function<GetInfo>("ADL2_Adapter_AdapterInfo_Get")(result.context, buffer, size * count) != 0) throw new InvalidOperationException();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<AdapterInfo>(buffer + i * size);
                    if (info.Exist == 0 || !string.Equals(info.Name.Trim(), adapter.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    result.adapterIndex = info.Index;
                    if (result.Sample() is not null) return result;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or InvalidOperationException) { }
        result.Dispose();
        return null;
    }
    public double? Sample()
    {
        var data = new LogData { Size = Marshal.SizeOf<LogData>(), Sensors = new Sensor[256] };
        if (query is null || query(context, adapterIndex, ref data) != 0) return null;
        var edge = data.Sensors[8];
        return edge.Supported != 0 && edge.Value is > 0 and < 150 ? edge.Value : null;
    }
    public void Dispose() { if (context != IntPtr.Zero) destroy?.Invoke(context); context = IntPtr.Zero; if (library != IntPtr.Zero) NativeLibrary.Free(library); library = IntPtr.Zero; }
}
