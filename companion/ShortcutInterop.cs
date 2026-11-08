using System.Runtime.InteropServices;

namespace ClawPal.Companion;

// System mute and volume through Core Audio's endpoint volume. Only Shortcuts tiles the user
// set up call this; the Media app keeps to the player's own mixer entry (AppAudio).
internal static class EndpointAudio
{
    private const int Render = 0, Capture = 1, Console = 0, Communications = 2;

    internal static bool MicrophoneMuted() => Read(Capture, Console) is { } muted && muted;
    internal static bool SpeakersMuted() => Read(Render, Console) is { } muted && muted;

    // A call app may use the communications microphone; muting means both, when they differ.
    internal static bool ToggleMicrophone()
    {
        if (Read(Capture, Console) is not { } muted) return false;
        bool changed = false;
        foreach (var volume in Volumes(Capture, [Console, Communications]))
            using (volume) { var context = Guid.Empty; changed |= volume.Value.SetMute(!muted, ref context) == 0; }
        return changed;
    }
    internal static bool ToggleSpeakers()
    {
        if (Read(Render, Console) is not { } muted) return false;
        bool changed = false;
        foreach (var volume in Volumes(Render, [Console]))
            using (volume) { var context = Guid.Empty; changed |= volume.Value.SetMute(!muted, ref context) == 0; }
        return changed;
    }
    // Like the keyboard's volume keys, a step also unmutes.
    internal static bool Step(float delta)
    {
        bool changed = false;
        foreach (var volume in Volumes(Render, [Console]))
            using (volume)
            {
                var context = Guid.Empty;
                if (volume.Value.GetMasterVolumeLevelScalar(out float level) != 0) continue;
                changed |= volume.Value.SetMasterVolumeLevelScalar(Math.Clamp(level + delta, 0, 1), ref context) == 0;
                volume.Value.SetMute(false, ref context);
            }
        return changed;
    }

    private static bool? Read(int flow, int role)
    {
        foreach (var volume in Volumes(flow, [role]))
            using (volume) return volume.Value.GetMute(out bool muted) == 0 ? muted : null;
        return null;
    }

    private sealed class Owned<T>(T value, params object[] owners) : IDisposable where T : class
    {
        public T Value { get; } = value;
        public void Dispose() { foreach (var owner in owners.Prepend(Value)) if (Marshal.IsComObject(owner)) Marshal.ReleaseComObject(owner); }
    }

    // One endpoint volume per distinct default device among the roles.
    private static List<Owned<IEndpointVolume>> Volumes(int flow, int[] roles)
    {
        var result = new List<Owned<IEndpointVolume>>();
        var ids = new HashSet<string>();
        IDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IDeviceEnumerator)new DeviceEnumerator();
            foreach (int role in roles)
            {
                if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null) continue;
                if (device.GetId(out var idPointer) != 0) { Marshal.ReleaseComObject(device); continue; }
                string id = Marshal.PtrToStringUni(idPointer) ?? ""; Marshal.FreeCoTaskMem(idPointer);
                var iid = typeof(IEndpointVolume).GUID;
                if (!ids.Add(id) || device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var activated) != 0 || activated is not IEndpointVolume volume)
                { Marshal.ReleaseComObject(device); continue; }
                result.Add(new(volume, device));
            }
        }
        catch (COMException error) { Log.Write("Endpoint volume unavailable: " + error.GetType().Name); }
        finally { if (enumerator is not null) Marshal.ReleaseComObject(enumerator); }
        return result;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IDevice? device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

// Keyboard input, locking and screen power for Shortcuts tiles.
internal static class NativeInput
{
    private const int KeyUp = 0x0002, ExtendedKey = 0x0001;

    // Presses the modifiers, the key, then lets go in reverse, as one SendInput batch.
    internal static bool Press(KeyCombo combo)
    {
        var keys = new List<Keys>();
        if (combo.Ctrl) keys.Add(Keys.LControlKey);
        if (combo.Alt) keys.Add(Keys.LMenu);
        if (combo.Shift) keys.Add(Keys.LShiftKey);
        if (combo.Win) keys.Add(Keys.LWin);
        keys.Add(combo.Key);
        var inputs = keys.Select(k => Key(k, false)).Concat(Enumerable.Reverse(keys).Select(k => Key(k, true))).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }
    private static Input Key(Keys key, bool up)
    {
        ushort vk = (ushort)key;
        int flags = (up ? KeyUp : 0) | (Extended(key) ? ExtendedKey : 0);
        return new Input { Type = 1, Keyboard = new KeyboardInput { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = flags } };
    }
    private static bool Extended(Keys key) => key is Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.NumLock or Keys.Divide or Keys.RControlKey or Keys.RMenu or Keys.LWin or Keys.RWin or Keys.Apps or
        Keys.PrintScreen or (>= Keys.BrowserBack and <= Keys.LaunchApplication2);

    internal static bool LockWorkStation() => LockWorkStationNative();
    // Asks every window's owner to turn the screens off; any input wakes them.
    internal static bool ScreensOff() => PostMessage(new IntPtr(0xFFFF), 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF170) /* SC_MONITORPOWER */, new IntPtr(2));

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Vk; public ushort Scan; public int Flags; public uint Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X; public int Y; public uint Data; public int Flags; public uint Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
        public KeyboardInput Keyboard { get => Union.Keyboard; set => Union.Keyboard = value; }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", EntryPoint = "LockWorkStation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockWorkStationNative();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

// An app's own icon, for its tile on the display and in the editor.
internal static class AppIcons
{
    public const int Size = 48;
    private static readonly Dictionary<string, Bitmap?> cache = new(StringComparer.OrdinalIgnoreCase);

    // The shell's 48 px icon for a file, shortcut or app, with its transparency.
    internal static Bitmap? Load(string path)
    {
        lock (cache)
        {
            if (cache.TryGetValue(path, out var known)) return known;
            Bitmap? icon = null;
            try { if (File.Exists(path) || Directory.Exists(path)) icon = Extract(path); }
            catch (Exception error) when (error is COMException or ArgumentException or ExternalException or InvalidCastException) { Log.Write("App icon unavailable: " + error.GetType().Name); }
            if (cache.Count > 64) cache.Clear();
            cache[path] = icon;
            return icon;
        }
    }

    // RGB565 little-endian with an alpha byte per pixel: LV_IMG_CF_TRUE_COLOR_ALPHA on the display.
    internal static byte[]? DisplayPixels(string path)
    {
        if (Load(path) is not { } icon) return null;
        var pixels = new byte[Size * Size * 3];
        lock (icon)
            for (int y = 0; y < Size; ++y)
                for (int x = 0; x < Size; ++x)
                {
                    var c = icon.GetPixel(x, y);
                    int value = ((c.R >> 3) << 11) | ((c.G >> 2) << 5) | (c.B >> 3), at = (y * Size + x) * 3;
                    pixels[at] = (byte)value; pixels[at + 1] = (byte)(value >> 8); pixels[at + 2] = c.A;
                }
        return pixels;
    }

    private static Bitmap? Extract(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0 || item is not IShellItemImageFactory factory) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = Size, Height = Size }, 0x4 /* SIIGBF_ICONONLY */ | 0x100 /* SIIGBF_SCALEUP */, out var handle) != 0) return null;
            try { return FromHandle(handle); }
            finally { DeleteObject(handle); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    // Reads the 32-bit DIB with its alpha; GDI+'s FromHbitmap would drop it.
    private static Bitmap? FromHandle(IntPtr handle)
    {
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = Size, Height = -Size, Planes = 1, BitCount = 32 };
        var bits = new byte[Size * Size * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        try { if (GetDIBits(screen, handle, 0, Size, bits, ref header, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        // The shell hands out premultiplied pixels; a channel brighter than its alpha means it did not.
        bool premultiplied = true;
        for (int i = 0; i < bits.Length && premultiplied; i += 4) premultiplied = bits[i] <= bits[i + 3] && bits[i + 1] <= bits[i + 3] && bits[i + 2] <= bits[i + 3];
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length && !anyAlpha; i += 4) anyAlpha = bits[i] != 0;
        var bitmap = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                int at = (y * Size + x) * 4, a = anyAlpha ? bits[at + 3] : 255;
                int Channel(int value) => premultiplied && anyAlpha && a > 0 ? Math.Min(255, value * 255 / a) : value;
                bitmap.SetPixel(x, y, Color.FromArgb(a, Channel(bits[at + 2]), Channel(bits[at + 1]), Channel(bits[at])));
            }
        return bitmap;
    }

    // Start menu shortcuts, by name, for the editor's app list.
    internal static IReadOnlyList<(string Name, string Path)> StartMenuApps()
    {
        var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) }
            .Where(f => f.Length > 0).Select(f => Path.Combine(f, "Programs")).Where(Directory.Exists);
        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                    apps.TryAdd(name, file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return apps.OrderBy(a => a.Key, StringComparer.CurrentCultureIgnoreCase).Select(a => (a.Key, a.Value)).ToList();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width; public int Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader { public int Size; public int Width; public int Height; public short Planes; public short BitCount; public int Compression; public int SizeImage; public int XPelsPerMeter; public int YPelsPerMeter; public int ClrUsed; public int ClrImportant; }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? item);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
