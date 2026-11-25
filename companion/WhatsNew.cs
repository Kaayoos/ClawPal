namespace ClawPal.Companion;

/* The short list of what this version of ClawPal added. It opens by itself once, the first time an
 * updated PC app starts, and again whenever the user asks for it in About. The list itself lives in
 * languages/*.json as whats_new_items, one line per change: rewrite it for every release. */
internal static class WhatsNew
{
    internal static string Version => typeof(WhatsNew).Assembly.GetName().Version?.ToString(3) ?? "";

    internal static IReadOnlyList<string> Items() =>
        L.Text("whats_new_items").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /* Due after an update: a set-up PC app that last showed the list for another version, or never did
     * (versions before this list existed). A fresh install marks its version as seen instead. */
    internal static bool Due(Configuration config) => Due(config.SetupComplete, config.SeenChangesVersion, Version);
    internal static bool Due(bool setupComplete, string seen, string version) =>
        setupComplete && version.Length > 0 && seen != version;

    internal static void MarkSeen(Configuration config)
    {
        if (config.SeenChangesVersion == Version) return;
        config.SeenChangesVersion = Version; config.Save();
    }
}
