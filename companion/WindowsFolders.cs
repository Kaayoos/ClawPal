using System;
using System.Runtime.InteropServices;

namespace ClawPal.Companion
{
    // Shared by the WinExe and PowerShell install/uninstall helpers via Add-Type.
    // Returning unredirected known-folder paths does not disable AppData filesystem
    // virtualization; ClawPal data and installation therefore live under Profile.
    public static class WindowsFolders
    {
        private const uint NoPackageRedirection = 0x00010000;
        private const uint DontVerify = 0x00004000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

        public static string Profile { get { return Get("5E6C858F-0E22-4760-9AFE-EA3317B67173"); } }
        public static string RoamingAppData { get { return Get("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"); } }
        public static string Startup { get { return Get("B97D20BB-F46A-4C97-BA10-5E3608430854"); } }
        public static string Programs { get { return Get("A77F5D77-2E2B-44C3-A6A2-ABA601054A51"); } }
        // Unredirected local AppData: identifies an older installation during an explicit
        // upgrade and locates Claude Desktop's data outside its package container.
        public static string LegacyLocalAppData { get { return Get("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"); } }

        private static string Get(string id)
        {
            Guid folder = new Guid(id);
            IntPtr pointer = IntPtr.Zero;
            try
            {
                int result = SHGetKnownFolderPath(ref folder, NoPackageRedirection | DontVerify, IntPtr.Zero, out pointer);
                Marshal.ThrowExceptionForHR(result);
                string path = Marshal.PtrToStringUni(pointer) ?? String.Empty;
                if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Windows returned an empty known-folder path.");
                return path;
            }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
        }
    }
}
