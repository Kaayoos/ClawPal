using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawPal.Companion;

internal readonly record struct SpotifyVolume(bool Available, int Volume, bool Muted);

// Spotify's own entry in the Windows volume mixer, read and changed through Core Audio.
// System volume and other applications are never touched.
internal static class SpotifyAudio
{
    internal static SpotifyVolume Read()
    {
        SpotifyVolume volume = default;
        Visit(audio =>
        {
            if (volume.Available || audio.GetMasterVolume(out float level) != 0 || audio.GetMute(out bool muted) != 0) return false;
            volume = new(true, (int)Math.Round(Math.Clamp(level, 0, 1) * 100), muted);
            return true;
        });
        return volume;
    }

    internal static bool SetVolume(int percent)
    {
        float level = Math.Clamp(percent, 0, 100) / 100f;
        var context = Guid.Empty;
        // Raising the slider from zero is a request to hear Spotify again.
        return Visit(audio => audio.SetMasterVolume(level, ref context) == 0 && (level == 0 || audio.SetMute(false, ref context) == 0)) > 0;
    }

    internal static bool ToggleMute()
    {
        var current = Read();
        var context = Guid.Empty;
        return current.Available && Visit(audio => audio.SetMute(!current.Muted, ref context) == 0) > 0;
    }

    // Visits every Spotify session on every active output, so a device switch or a
    // second Spotify stream cannot leave one session behind. Returns the successes.
    private static int Visit(Func<ISimpleAudioVolume, bool> use)
    {
        int succeeded = 0;
        var released = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            released.Add(enumerator);
            if (enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out var devices) != 0) return 0;
            released.Add(devices);
            if (devices.GetCount(out uint deviceCount) != 0) return 0;
            using var current = Process.GetCurrentProcess();
            int session = current.SessionId;
            for (uint d = 0; d < Math.Min(deviceCount, 16u); ++d)
            {
                if (devices.Item(d, out var device) != 0) continue;
                released.Add(device);
                var managerId = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref managerId, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object activated) != 0) continue;
                released.Add(activated);
                var manager = (IAudioSessionManager2)activated;
                if (manager.GetSessionEnumerator(out var sessions) != 0) continue;
                released.Add(sessions);
                if (sessions.GetCount(out int sessionCount) != 0) continue;
                for (int s = 0; s < Math.Min(sessionCount, 128); ++s)
                {
                    if (sessions.GetSession(s, out var control) != 0) continue;
                    released.Add(control);
                    if (control is not IAudioSessionControl2 details || details.GetProcessId(out uint processId) != 0 ||
                        !IsSpotify((int)processId, session) || control is not ISimpleAudioVolume audio) continue;
                    if (use(audio)) ++succeeded;
                }
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException) { }
        finally
        {
            foreach (var item in released) if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
        return succeeded;
    }

    private static bool IsSpotify(int processId, int session)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.SessionId == session && process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
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
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager slots precede the enumerator in the vtable.
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
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl slots, unused here, keep the vtable aligned.
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
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
