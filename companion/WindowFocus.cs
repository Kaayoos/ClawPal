using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawPal.Companion;

// "Show on PC" from the display's needs-you alert: brings forward the window of the
// Claude Code session that waits for an answer, or the Codex desktop app.
internal static class WindowFocus
{
    internal static MusicControlResult Execute(DeviceControl command)
    {
        bool ok = command.Value switch
        {
            Protocol.ClaudeApp => ClaudeUsage.ReadSessions().Where(s => !s.Ended && s.State == "waiting" && ClaudeBridge.IsAlive(s))
                .OrderByDescending(s => s.ActivityAt).Any(s => Bring(WindowFor(s.ProcessId, s.ProcessStarted))),
            Protocol.CodexApp => Codex(),
            _ => false,
        };
        return new(command.Id, ok, ok ? "" : "unavailable");
    }

    private static bool Codex()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName("Codex");
            using var current = Process.GetCurrentProcess();
            int session = current.SessionId;
            return processes.Where(p => p.SessionId == session).Any(p => Bring(p.MainWindowHandle));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    // The CLI rarely owns a window: Windows Terminal, VS Code or Claude Desktop does. Walk up
    // the parent chain to the first visible window; a classic console window counts too.
    internal static IntPtr WindowFor(int processId, long started)
    {
        int id = processId;
        for (int depth = 0; depth < 8 && id > 0; ++depth)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (depth == 0 && started != 0 && process.StartTime.ToUniversalTime().Ticks != started) return IntPtr.Zero;
                if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                if (process.MainWindowHandle != IntPtr.Zero && IsWindowVisible(process.MainWindowHandle)) return process.MainWindowHandle;
                if (depth == 0 && ConsoleWindow(id) is var console && console != IntPtr.Zero && IsWindowVisible(console)) return console;
                id = ClaudeBridge.ParentProcessId(process);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { break; }
        }
        return IntPtr.Zero;
    }

    // Terminals that host the console themselves hand out a hidden pseudo window here.
    private static IntPtr ConsoleWindow(int processId)
    {
        lock (typeof(WindowFocus))
        {
            if (!AttachConsole((uint)processId)) return IntPtr.Zero;
            try { return GetConsoleWindow(); }
            finally { FreeConsole(); }
        }
    }

    // A background process may not take the foreground; sharing input with the current
    // foreground thread for a moment is allowed. If Windows still refuses, flash the taskbar.
    internal static bool Bring(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        if (GetAncestor(window, 3 /* GA_ROOTOWNER */) is var root && root != IntPtr.Zero) window = root;
        if (IsIconic(window)) ShowWindow(window, 9 /* SW_RESTORE */);
        uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
        if (GetForegroundWindow() == window) return true;
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 3 | 12 /* ALL | TIMERNOFG */, Count = 0, Timeout = 0 };
        FlashWindowEx(ref flash);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool enable);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
}
