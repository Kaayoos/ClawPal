using System.Text.Json;

namespace ClawPal.Companion;

/* One look as both applications draw it. The display firmware is generated from the
 * same themes/themes.json, so a colour changed there changes on both sides. */
public sealed record ThemeLook(string Id, string AccentId, bool Mascot, bool HasAccents,
    Color Background, Color Surface, Color Hover, Color Border, Color Ink, Color Muted, Color Accent, Color Warning, Color WeatherApp)
{
    /* Cased looks sit the display's screen in ClawPal's orange case, with square shapes and heavy type,
     * as this app does; Frame is the case and FrameInk the ink printed on it. */
    public bool Cased { get; init; }
    public Color Frame { get; init; }
    public Color FrameInk { get; init; }
    public string Name => L.Text($"theme_{Id}");
    public string Description => L.Text($"theme_{Id}_description");
}

public static class Themes
{
    public const string Default = "crab", DefaultAccent = "champagne";
    private sealed record Catalog(string[] Ids, string[] AccentIds, Dictionary<string, Color> Accents, Dictionary<string, JsonElement> Themes);
    private static readonly Lazy<Catalog> catalog = new(() => {
        using var stream = typeof(Themes).Assembly.GetManifestResourceStream("ClawPal.Themes") ?? throw new IOException("Missing theme catalog");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var accents = root.GetProperty("accents").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!, a => Parse(a.GetProperty("color")));
        var themes = root.GetProperty("themes").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!, t => t.Clone());
        if (themes.Keys.FirstOrDefault() != Default || !accents.ContainsKey(DefaultAccent)) throw new IOException("Invalid theme catalog");
        return new(themes.Keys.ToArray(), accents.Keys.ToArray(), accents, themes);
    });
    public static IReadOnlyList<string> Ids => catalog.Value.Ids;
    public static IReadOnlyList<string> AccentIds => catalog.Value.AccentIds;
    public static bool Supports(string? theme, string? accent) => theme is not null && accent is not null && catalog.Value.Themes.ContainsKey(theme) && catalog.Value.Accents.ContainsKey(accent);
    public static Color AccentColor(string accent) => catalog.Value.Accents[accent];
    /* Unknown names fall back to the crab, as the firmware does for older settings. */
    public static ThemeLook Get(string? theme, string? accent)
    {
        var value = catalog.Value;
        string id = theme is not null && value.Themes.ContainsKey(theme) ? theme : Default;
        string accentId = accent is not null && value.Accents.ContainsKey(accent) ? accent : DefaultAccent;
        var element = value.Themes[id];var colors = element.GetProperty("colors");var flags = element.GetProperty("flags");
        Color Role(string name) => Parse(colors.GetProperty(name));
        bool hasAccents = flags.GetProperty("accents").GetBoolean();
        return new(id, accentId, flags.GetProperty("mascot").GetBoolean(), hasAccents, Role("bg"), Role("surface"), Role("pressed"), Role("rule"),
            Role("text"), Role("muted"), hasAccents ? value.Accents[accentId] : Role("accent"), Role("warning"), Role("weather_app"))
            { Cased = flags.GetProperty("cased").GetBoolean(), Frame = Role("frame"), FrameInk = Role("frame_ink") };
    }
    private static Color Parse(JsonElement value)
    {
        string text = value.GetString() ?? "";
        if (text.Length != 7 || text[0] != '#') throw new IOException("Invalid theme colour");
        return Color.FromArgb(Convert.ToInt32(text[1..], 16) | unchecked((int)0xFF000000));
    }
}
