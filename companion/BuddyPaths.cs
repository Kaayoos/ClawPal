using System;
using System.IO;

namespace ClawPal.Companion
{
    // No LocalAppData, package cache or current-directory fallback is allowed:
    // a packaged child and Explorer must derive the same per-user files.
    public static class BuddyPaths
    {
        public static string DataDirectory(string profile)
        {
            if (String.IsNullOrWhiteSpace(profile) || !Path.IsPathRooted(profile))
                throw new ArgumentException("A rooted Windows profile path is required.", "profile");
            string root = Path.GetFullPath(profile);
            if (String.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                (Path.GetPathRoot(root) ?? String.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A user profile cannot be a filesystem root.", "profile");
            return Path.Combine(root, ".clawpal");
        }

        public static string InstalledDirectory(string profile) { return Path.Combine(DataDirectory(profile), "app"); }
        public static string InstalledExecutable(string profile) { return Path.Combine(InstalledDirectory(profile), "ClawPal.Companion.exe"); }
    }
}
