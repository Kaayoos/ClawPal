namespace ClawPal.Companion;

// Pure launch/path decisions are separate from Windows shortcut/registry writes.
public static class StartupContract
{
    public static bool ShouldShowSettings(bool setupComplete,bool paired) => setupComplete && paired;
    public const string StartupArgument = "--startup-launch";
    public static bool IsNormalLaunch(string[] args) => args.Length == 0 || (args.Length == 1 && args[0] == StartupArgument);

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool ShouldRedirect(string[] args, string? currentPath, string installedPath, bool installedExists) =>
        IsNormalLaunch(args) && installedExists && !SamePath(currentPath, installedPath);

    public static bool ValidStartupLink(string? target, string? arguments, string installedPath, bool installedExists) =>
        installedExists && SamePath(target, installedPath) && arguments == StartupArgument;

    public static string Origin(string[] args)
    {
        if (args.Contains(StartupArgument)) return "windows-startup";
        if (args.Any(arg => arg is "--diagnostics" or "--music-diagnostics" or "--codex-diagnostics" or "--activity-diagnostics" or "--game-diagnostics" or "--test-connection")) return "diagnostic";
        if (args.Any(arg => arg is "--configure-only" or "--pair-token-file" or "--set-startup" or "--remove-startup" or "--create-start-menu")) return "configuration";
        return "manual";
    }
}
