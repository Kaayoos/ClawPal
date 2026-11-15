using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawPal.Companion;

// How an app's channel looks on the display: a name its fonts can draw and a colour from its icon.
internal static class MixerLooks
{
    // A name the display can draw, short enough for its channel.
    public static string Fit(string name)
    {
        var builder = new StringBuilder();
        foreach (var rune in name.Trim().EnumerateRunes())
        {
            var text = rune.ToString();
            if (Encoding.UTF8.GetByteCount(builder + text) > MixerContract.MaxNameBytes) break;
            if (!Rune.IsControl(rune) && DisplayGlyphs.Covers(text)) builder.Append(text);
        }
        return builder.ToString().Trim();
    }

    // The palette colour nearest the icon's most common strong hue, or grey for a plain icon.
    public static int ColorFor(Bitmap icon)
    {
        var buckets = new double[36];
        double strong = 0, opaque = 0;
        lock (icon)
            for (int y = 0; y < icon.Height; ++y)
                for (int x = 0; x < icon.Width; ++x)
                {
                    var c = icon.GetPixel(x, y);
                    if (c.A < 128) continue;
                    opaque += 1;
                    double saturation = c.GetSaturation(), brightness = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
                    if (saturation < 0.35 || brightness < 0.3) continue;
                    strong += 1;
                    buckets[(int)(c.GetHue() / 10) % 36] += saturation;
                }
        if (opaque == 0 || strong / opaque < 0.12) return MixerContract.Grey;
        int best = Array.IndexOf(buckets, buckets.Max());
        return MixerContract.Nearest(best * 10 + 5);
    }

}

internal sealed class AudioMixer
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> names = new();
    private readonly Dictionary<string, (string Name, int Color)> looks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(uint Pid, long Created), (string Process, string? Path)> processes = new();
    private int nextId = MixerContract.FirstApp;
    private Dictionary<int, string> paths = new();

    // The executable behind each app channel in the latest snapshot, for its icon.
    public IReadOnlyDictionary<int, string> IconPaths { get { lock (gate) return new Dictionary<int, string>(paths); } }

    public MixerStats Snapshot() => CoreAudio.Run(SnapshotNow);
    private MixerStats SnapshotNow()
    {
        var channels = new List<MixerChannel>();
        var active = new HashSet<int>();
        var seenPaths = new Dictionary<int, string>();
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        lock (gate)
        {
            if (Endpoint(Render) is { } output) channels.Add(new(MixerContract.OutputId, MixerContract.Output, null, output.Volume, output.Muted));
            var groups = new Dictionary<int, (int Volume, bool Muted, bool Preferred)>();
            VisitSessions((device, isDefault, control, audio) =>
            {
                if (control.GetState(out int state) != 0 || state == 2 /* expired */) return;
                bool system = control.IsSystemSoundsSession() == 0;
                int id;
                if (system) id = MixerContract.SystemId;
                else
                {
                    if (control.GetProcessId(out uint pid) != 0 || pid == 0 || ProcessFor(pid, session) is not { } process) return;
                    id = IdFor(process.Process);
                    if (process.Path is { } path) seenPaths.TryAdd(id, path);
                    if (!looks.ContainsKey(process.Process)) looks[process.Process] = Look(process.Process, process.Path);
                }
                if (state == 1) active.Add(id);
                if (audio.GetMasterVolume(out float level) != 0 || audio.GetMute(out bool muted) != 0) return;
                // The default device's session speaks for the app; another device's only when it has none there.
                if (groups.TryGetValue(id, out var known) && (known.Preferred || !isDefault)) return;
                groups[id] = ((int)Math.Round(Math.Clamp(level, 0, 1) * 100), muted, isDefault);
            });
            foreach (var (id, value) in groups.OrderBy(g => g.Key))
            {
                if (id == MixerContract.SystemId) { channels.Add(new(id, MixerContract.System, null, value.Volume, value.Muted, MixerContract.Grey)); continue; }
                if (!names.TryGetValue(id, out var process) || !looks.TryGetValue(process, out var look)) continue;
                channels.Add(new(id, MixerContract.App, look.Name, value.Volume, value.Muted, look.Color));
            }
            if (Endpoint(Capture) is { } mic) channels.Add(new(MixerContract.MicId, MixerContract.Mic, null, mic.Volume, mic.Muted, MixerContract.Teal));
            paths = seenPaths;
            // Processes that are gone do not need their creation times remembered.
            if (processes.Count > 256) processes.Clear();
        }
        return new(MixerContract.Limit(channels, active));
    }

    public MusicControlResult Execute(DeviceControl command) => CoreAudio.Run(() => ExecuteNow(command));
    private MusicControlResult ExecuteNow(DeviceControl command)
    {
        bool ok;
        lock (gate)
        {
            if (command.Action == "mixer_volume")
            {
                var (id, percent) = MixerContract.Volume(command.Value);
                float level = percent / 100f;
                // Raising a fader is asking to hear it, so it unmutes too, as the display shows.
                ok = Apply(id, (endpoint, context) => endpoint.SetMasterVolumeLevelScalar(level, ref context) == 0 && (percent == 0 || endpoint.SetMute(false, ref context) == 0),
                    (audio, context) => audio.SetMasterVolume(level, ref context) == 0 && (percent == 0 || audio.SetMute(false, ref context) == 0));
            }
            else
            {
                var (id, muted) = MixerContract.Mute(command.Value);
                ok = Apply(id, (endpoint, context) => endpoint.SetMute(muted, ref context) == 0, (audio, context) => audio.SetMute(muted, ref context) == 0);
            }
        }
        return new(command.Id, ok, ok ? "" : "unavailable");
    }

    private bool Apply(int id, Func<EndpointAudio.IEndpointVolume, Guid, bool> endpoint, Func<ISimpleAudioVolume, Guid, bool> session)
    {
        if (id == MixerContract.OutputId || id == MixerContract.MicId)
        {
            bool changed = false;
            WithEndpoint(id == MixerContract.OutputId ? Render : Capture, volume => changed = endpoint(volume, Guid.Empty));
            return changed;
        }
        string? process = null;
        if (id != MixerContract.SystemId && !names.TryGetValue(id, out process)) return false;
        using var current = Process.GetCurrentProcess();
        int userSession = current.SessionId, succeeded = 0;
        VisitSessions((device, isDefault, control, audio) =>
        {
            bool system = control.IsSystemSoundsSession() == 0;
            if (process is null ? !system : system || control.GetProcessId(out uint pid) != 0 ||
                ProcessFor(pid, userSession) is not { } found || !found.Process.Equals(process, StringComparison.OrdinalIgnoreCase)) return;
            if (session(audio, Guid.Empty)) ++succeeded;
        });
        return succeeded > 0;
    }

    private int IdFor(string process)
    {
        if (ids.TryGetValue(process, out int id)) return id;
        if (nextId > MixerContract.MaxId) { ids.Clear(); names.Clear(); nextId = MixerContract.FirstApp; }
        id = nextId++;
        ids[process] = id;
        names[id] = process;
        return id;
    }

    // The app's own description ("Google Chrome"), else its product or process name; its colour
    // comes from its icon.
    private static (string Name, int Color) Look(string process, string? path)
    {
        string name = "";
        if (path is not null)
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = MixerLooks.Fit(info.FileDescription ?? "");
                if (name.Length == 0) name = MixerLooks.Fit(info.ProductName ?? "");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (name.Length == 0) name = MixerLooks.Fit(process.Length > 0 ? char.ToUpperInvariant(process[0]) + process[1..] : process);
        if (name.Length == 0) name = "App";
        int color = path is not null && AppIcons.Load(path) is { } icon ? MixerLooks.ColorFor(icon) : MixerContract.ColorFor(name);
        return (name, color);
    }

    private (string Process, string? Path)? ProcessFor(uint pid, int session)
    {
        if (!ProcessIdToSessionId(pid, out uint owner) || owner != session) return null;
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return null;
            if (processes.TryGetValue((pid, created), out var known)) return known;
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            string? path = QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            string name = path is not null ? Path.GetFileNameWithoutExtension(path) : "";
            if (name.Length == 0)
                try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
            var value = (name, path);
            processes[(pid, created)] = value;
            return value;
        }
        finally { CloseHandle(handle); }
    }

    private const int Render = 0, Capture = 1;

    private static (int Volume, bool Muted)? Endpoint(int flow)
    {
        (int, bool)? result = null;
        WithEndpoint(flow, volume =>
        {
            if (volume.GetMasterVolumeLevelScalar(out float level) == 0 && volume.GetMute(out bool muted) == 0)
                result = ((int)Math.Round(Math.Clamp(level, 0, 1) * 100), muted);
        });
        return result;
    }

    private static void WithEndpoint(int flow, Action<EndpointAudio.IEndpointVolume> use)
    {
        var released = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            released.Add(enumerator);
            if (enumerator.GetDefaultAudioEndpoint(flow, 0 /* console */, out var device) != 0 || device is null) return;
            released.Add(device);
            var iid = typeof(EndpointAudio.IEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object activated) != 0) return;
            released.Add(activated);
            if (activated is EndpointAudio.IEndpointVolume volume) use(volume);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException) { Report(error); }
        finally { foreach (var item in released) if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
    }

    // Every session on every active output, told whether its device is the default one.
    private static void VisitSessions(Action<string, bool, IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        var released = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            released.Add(enumerator);
            string defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(Render, 0, out var preferred) == 0 && preferred is not null)
            {
                released.Add(preferred);
                defaultId = DeviceId(preferred);
            }
            if (enumerator.EnumAudioEndpoints(Render, 1 /* active */, out var devices) != 0) return;
            released.Add(devices);
            if (devices.GetCount(out uint deviceCount) != 0) return;
            for (uint d = 0; d < Math.Min(deviceCount, 16u); ++d)
            {
                if (devices.Item(d, out var device) != 0) continue;
                released.Add(device);
                string id = DeviceId(device);
                var managerId = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref managerId, 23, IntPtr.Zero, out object activated) != 0) continue;
                released.Add(activated);
                if (((IAudioSessionManager2)activated).GetSessionEnumerator(out var sessions) != 0) continue;
                released.Add(sessions);
                if (sessions.GetCount(out int sessionCount) != 0) continue;
                for (int s = 0; s < Math.Min(sessionCount, 128); ++s)
                {
                    if (sessions.GetSession(s, out var control) != 0) continue;
                    released.Add(control);
                    if (control is IAudioSessionControl2 details && control is ISimpleAudioVolume audio) visit(id, id == defaultId, details, audio);
                }
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException) { Report(error); }
        finally { foreach (var item in released) if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
    }

    // Core Audio failures are logged once each, so a broken audio stack is visible without
    // writing the same line every second.
    private static readonly HashSet<string> reported = new();
    private static void Report(Exception error)
    {
        lock (reported) if (!reported.Add(error.GetType().Name + error.TargetSite?.Name)) return;
        Log.Write("Audio mixer: " + error);
    }

    private static string DeviceId(IMMDevice device)
    {
        if (device.GetId(out var pointer) != 0) return "";
        try { return Marshal.PtrToStringUni(pointer) ?? ""; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr identifier);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr identifier);
        [PreserveSig] int GetProcessId(out uint processId);
        // S_OK (0) for Windows' own sounds, S_FALSE (1) otherwise.
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
