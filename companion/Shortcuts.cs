using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawPal.Companion;

public enum ShortcutTarget { None, App, Website, Folder, Keys }

// What a tile can do. The display only ever learns how a tile looks; this PC keeps what it runs.
public sealed record ShortcutAction(string Id, string Group, string Icon, string Color, string LabelKey = "", string LabelOnKey = "",
    bool Toggle = false, bool LockedOk = false, bool Repeat = false, ShortcutTarget Target = ShortcutTarget.None)
{
    public string Name => L.Text($"shortcut_{Id}");
}

public static class ShortcutCatalog
{
    // Groups appear in this order in the editor. Locked-safe actions keep working while Windows is locked.
    public static readonly string[] Groups = ["sound", "media", "windows", "open", "keyboard"];
    public static readonly ShortcutAction[] Actions = [
        new("mute_mic", "sound", "mic", "red", "mute_mic", "mic_muted", Toggle: true, LockedOk: true),
        new("mute_sound", "sound", "speaker", "amber", "mute_sound", "sound_muted", Toggle: true, LockedOk: true),
        new("volume_down", "sound", "volume_down", "accent", "volume_down", LockedOk: true, Repeat: true),
        new("volume_up", "sound", "volume_up", "accent", "volume_up", LockedOk: true, Repeat: true),
        new("play_pause", "media", "play_pause", "green", "play_pause", LockedOk: true),
        new("next", "media", "next", "green", "next", LockedOk: true),
        new("previous", "media", "previous", "green", "previous", LockedOk: true),
        new("lock", "windows", "lock", "blue", "lock_pc", LockedOk: true),
        new("screens_off", "windows", "screen_off", "violet", "screens_off", LockedOk: true),
        new("show_desktop", "windows", "desktop", "blue", "show_desktop"),
        new("screenshot", "windows", "screenshot", "violet", "screenshot"),
        new("task_manager", "windows", "task_manager", "grey", "task_manager"),
        new("open_app", "open", "app", "accent", Target: ShortcutTarget.App),
        new("open_website", "open", "globe", "teal", Target: ShortcutTarget.Website),
        new("open_folder", "open", "folder", "amber", Target: ShortcutTarget.Folder),
        new("keys", "keyboard", "keyboard", "pink", Target: ShortcutTarget.Keys),
    ];
    public static ShortcutAction? Find(string? id) => Actions.FirstOrDefault(a => a.Id == id);
    public static ShortcutAction Get(string id) => Find(id) ?? throw new ArgumentException(L.Text("choose_what_this_shortcut_does"));
}

// shortcuts/shortcuts.json, shared with the firmware: the icon line art and the tile colours.
public static class ShortcutArt
{
    public sealed record Icon(string Id, string? On, JsonElement[] Shapes);
    public sealed record Swatch(string Id, Color? Value);
    private static readonly Lazy<(Icon[] Icons, Swatch[] Colors)> data = new(Load);
    public static IReadOnlyList<Icon> Icons => data.Value.Icons;
    public static IReadOnlyList<Swatch> Colors => data.Value.Colors;
    public static Icon? FindIcon(string? id) => Icons.FirstOrDefault(i => i.Id == id);
    public static bool HasColor(string? id) => Colors.Any(c => c.Id == id);
    // Accent follows the look, as on the display.
    public static Color ColorOf(string? id) => Colors.FirstOrDefault(c => c.Id == id)?.Value ?? ClawTheme.Accent;
    private static (Icon[], Swatch[]) Load()
    {
        using var stream = typeof(ShortcutArt).Assembly.GetManifestResourceStream("ClawPal.Shortcuts") ?? throw new InvalidOperationException("Shortcut art is missing.");
        using var document = JsonDocument.Parse(stream);
        var icons = document.RootElement.GetProperty("icons").EnumerateArray().Select(i => new Icon(i.GetProperty("id").GetString()!,
            i.TryGetProperty("on", out var on) ? on.GetString() : null, i.GetProperty("shapes").EnumerateArray().Select(s => s.Clone()).ToArray())).ToArray();
        var colors = document.RootElement.GetProperty("colors").EnumerateArray().Select(c => new Swatch(c.GetProperty("id").GetString()!,
            c.TryGetProperty("color", out var value) ? ColorTranslator.FromHtml(value.GetString()!) : null)).ToArray();
        return (icons, colors);
    }
    // Draws an icon from its line art on the 0..100 grid, as buddy_shortcuts_page.c does on the display.
    public static void Draw(Graphics g, string iconId, RectangleF bounds, Color ink)
    {
        if (FindIcon(iconId) is not { } icon) return;
        float size = Math.Min(bounds.Width, bounds.Height), x0 = bounds.X + (bounds.Width - size) / 2, y0 = bounds.Y + (bounds.Height - size) / 2;
        float width = size >= 40 ? 3 * size / 48f : Math.Max(1.5f, 2 * size / 32f);
        PointF P(int x, int y) => new(x0 + x * size / 100f, y0 + y * size / 100f);
        var smoothing = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(ink, width) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var fill = new SolidBrush(ink);
        foreach (var shape in icon.Shapes)
        {
            var kind = shape[0].GetString();
            var v = shape.EnumerateArray().Skip(1).Select(e => e.GetInt32()).ToArray();
            switch (kind)
            {
                case "l": g.DrawLine(pen, P(v[0], v[1]), P(v[2], v[3])); break;
                case "p": g.DrawLines(pen, Enumerable.Range(0, v.Length / 2).Select(i => P(v[2 * i], v[2 * i + 1])).ToArray()); break;
                case "b" or "f":
                {
                    var r = new RectangleF(P(v[0], v[1]), new SizeF(v[2] * size / 100f, v[3] * size / 100f));
                    float radius = v[4] < 0 ? Math.Min(r.Width, r.Height) / 2 : v[4] * size / 100f;
                    using var path = Rounded(r, radius);
                    if (kind == "f") g.FillPath(fill, path); else g.DrawPath(pen, path);
                    break;
                }
                case "a":
                {
                    float r = v[2] * size / 100f, sweep = (v[4] - v[3] + 360) % 360;
                    if (sweep == 0) sweep = 360;
                    var c = P(v[0], v[1]);
                    g.DrawArc(pen, c.X - r, c.Y - r, 2 * r, 2 * r, v[3], sweep);
                    break;
                }
            }
        }
        g.SmoothingMode = smoothing;
    }
    private static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Characters the display's fonts can draw (firmware/main/language/font_coverage.json).
public static class DisplayGlyphs
{
    private static readonly Lazy<HashSet<int>> coverage = new(() =>
    {
        using var stream = typeof(DisplayGlyphs).Assembly.GetManifestResourceStream("ClawPal.FontCoverage") ?? throw new InvalidOperationException("Font coverage is missing.");
        return JsonSerializer.Deserialize<int[]>(stream)!.ToHashSet();
    });
    public static bool Covers(string text) => text.EnumerateRunes().All(r => coverage.Value.Contains(r.Value));
}

public sealed record ShortcutTile(int Slot, string Action, string Label = "", string Icon = "", string Color = "",
    string Target = "", string Keys = "", bool AppIcon = false)
{
    [JsonIgnore] public ShortcutAction Kind => ShortcutCatalog.Get(Action);
    // Open app tiles show the app's own icon unless their owner picked one of ours.
    [JsonIgnore] public bool UsesAppIcon => Action == "open_app" && AppIcon;
}

public sealed record ShortcutDeck(int Size, ShortcutTile[] Tiles)
{
    public static readonly int[] Sizes = [6, 8, 12, 16];
    public const int MaxLabel = 24, MaxLabelBytes = 48;

    public ShortcutTile? At(int slot) => Tiles.FirstOrDefault(t => t.Slot == slot);
    public ShortcutDeck Set(int slot, ShortcutTile? tile)
    {
        var tiles = Tiles.Where(t => t.Slot != slot).ToList();
        if (tile is not null) tiles.Add(tile with { Slot = slot });
        return this with { Tiles = tiles.OrderBy(t => t.Slot).ToArray() };
    }
    // Dragging onto a used slot swaps the two tiles; onto an empty one, moves it.
    public ShortcutDeck Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Size || to >= Size || At(from) is not { } moving) return this;
        var other = At(to);
        return Set(to, null).Set(from, null).Set(to, moving).Set(from, other);
    }
    // A smaller grid keeps its tiles by moving them into the first free slots it still has.
    public ShortcutDeck Resize(int size)
    {
        if (!Sizes.Contains(size) || Tiles.Length > size) throw new ArgumentException(L.Text("remove_shortcuts_before_making_the_grid_smaller"));
        var kept = Tiles.Where(t => t.Slot < size).ToList();
        var free = Enumerable.Range(0, size).Where(s => kept.All(t => t.Slot != s)).GetEnumerator();
        foreach (var tile in Tiles.Where(t => t.Slot >= size)) { free.MoveNext(); kept.Add(tile with { Slot = free.Current }); }
        return new(size, kept.OrderBy(t => t.Slot).ToArray());
    }

    public void Validate()
    {
        if (!Sizes.Contains(Size) || Tiles is null || Tiles.Length > Size || Tiles.Select(t => t.Slot).Distinct().Count() != Tiles.Length)
            throw new ArgumentException(L.Text("shortcuts_need_a_free_slot_each"));
        foreach (var tile in Tiles)
        {
            if (tile.Slot < 0 || tile.Slot >= Size) throw new ArgumentException(L.Text("shortcuts_need_a_free_slot_each"));
            var action = ShortcutCatalog.Get(tile.Action);
            if (ShortcutArt.FindIcon(tile.Icon) is null || !ShortcutArt.HasColor(tile.Color)) throw new ArgumentException(L.Text("choose_an_icon_and_colour"));
            if (tile.Label.Length > MaxLabel || Encoding.UTF8.GetByteCount(tile.Label) > MaxLabelBytes || tile.Label.Any(char.IsControl))
                throw new ArgumentException(L.Text("shortcut_labels_are_up_to_24_characters"));
            if (!DisplayGlyphs.Covers(tile.Label)) throw new ArgumentException(L.Text("the_display_cannot_show_some_characters_in_label", tile.Label));
            switch (action.Target)
            {
                case ShortcutTarget.App when string.IsNullOrWhiteSpace(tile.Target) || !Path.IsPathFullyQualified(tile.Target):
                    throw new ArgumentException(L.Text("choose_an_app_for_this_shortcut"));
                case ShortcutTarget.Folder when string.IsNullOrWhiteSpace(tile.Target) || !Path.IsPathFullyQualified(tile.Target):
                    throw new ArgumentException(L.Text("choose_a_folder_for_this_shortcut"));
                case ShortcutTarget.Website when !IsWebAddress(tile.Target):
                    throw new ArgumentException(L.Text("enter_a_web_address_starting_with_http"));
                case ShortcutTarget.Keys when KeyCombo.Parse(tile.Keys) is null:
                    throw new ArgumentException(L.Text("record_a_key_combination"));
            }
        }
    }
    public static bool IsWebAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;

    // What the display receives: looks only. Anything that runs stays here.
    private sealed record DisplayTile(int Slot, string Label, string Icon, string Color,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string LabelOn = "",
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Toggle = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LockedOk = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AppIcon = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Repeat = false);
    private sealed record DisplayDeck(int Rev, int Size, DisplayTile[] Tiles);
    private DisplayTile[] DisplayTiles(string displayLanguage) => Tiles.OrderBy(t => t.Slot).Select(t =>
    {
        var kind = t.Kind;
        return new DisplayTile(t.Slot, t.Label, t.Icon, t.Color, kind.LabelOnKey.Length > 0 ? L.DisplayText(displayLanguage, kind.LabelOnKey) : "",
            kind.Toggle, kind.LockedOk, t.UsesAppIcon, kind.Repeat);
    }).ToArray();
    // 1..65535, from everything the display shows and every target, so any edit reaches the display.
    public int Revision(string displayLanguage)
    {
        var text = JsonSerializer.Serialize(new { size = Size, tiles = DisplayTiles(displayLanguage), targets = Tiles.Select(t => t.Target + "\n" + t.Keys) }, Protocol.Json);
        return BinaryPrimitives.ReadUInt16LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(text))) % Protocol.ShortcutMaxRev + 1;
    }
    public byte[] DisplayJson(string displayLanguage) =>
        JsonSerializer.SerializeToUtf8Bytes(new DisplayDeck(Revision(displayLanguage), Size, DisplayTiles(displayLanguage)), Protocol.Json);

    // Safe, useful tiles for everyone, labelled in the display's language. Home is left alone.
    public static ShortcutDeck Starter(string displayLanguage)
    {
        ShortcutTile Tile(int slot, string action)
        {
            var kind = ShortcutCatalog.Get(action);
            return new(slot, action, L.DisplayText(displayLanguage, kind.LabelKey), kind.Icon, kind.Color);
        }
        return new(8, [Tile(0, "mute_mic"), Tile(1, "mute_sound"), Tile(2, "volume_down"), Tile(3, "volume_up"),
            Tile(4, "play_pause"), Tile(5, "next"), Tile(6, "lock"), Tile(7, "screenshot")]);
    }
    // A fresh tile for an action, labelled the way the display will show it.
    public static ShortcutTile NewTile(int slot, string action, string displayLanguage, string target = "", string keys = "")
    {
        var kind = ShortcutCatalog.Get(action);
        string label = kind.Target switch
        {
            ShortcutTarget.App => Path.GetFileNameWithoutExtension(target),
            ShortcutTarget.Folder => Path.GetFileName(Path.TrimEndingDirectorySeparator(target)) is { Length: > 0 } name ? name : target,
            ShortcutTarget.Website => Uri.TryCreate(target, UriKind.Absolute, out var uri) ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host) : "",
            ShortcutTarget.Keys => KeyCombo.Parse(keys)?.Text ?? "",
            _ => L.DisplayText(displayLanguage, kind.LabelKey),
        };
        return new(slot, action, Fit(label), kind.Icon, kind.Color, target, keys, kind.Target == ShortcutTarget.App);
    }
    // Labels the display can show: trimmed to length, with characters outside its fonts dropped.
    public static string Fit(string label)
    {
        var builder = new StringBuilder();
        foreach (var rune in label.Trim().EnumerateRunes())
        {
            var text = rune.ToString();
            if (builder.Length + text.Length > MaxLabel || Encoding.UTF8.GetByteCount(builder + text) > MaxLabelBytes) break;
            if (!Rune.IsControl(rune) && DisplayGlyphs.Covers(text)) builder.Append(text);
        }
        return builder.ToString().Trim();
    }
}

// A key combination such as Ctrl+Shift+M, stored as that text.
public sealed record KeyCombo(bool Ctrl, bool Alt, bool Shift, bool Win, Keys Key)
{
    private static readonly Keys[] Modifiers = [Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey,
        Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin, Keys.None];
    public static bool IsModifier(Keys key) => Modifiers.Contains(key);
    public string Value => string.Join("+", Parts(KeyName(Key, false)));
    public string Text => string.Join(" + ", Parts(KeyName(Key, true)));
    private IEnumerable<string> Parts(string key)
    {
        if (Ctrl) yield return "Ctrl";
        if (Alt) yield return "Alt";
        if (Shift) yield return "Shift";
        if (Win) yield return "Win";
        yield return key;
    }
    private static string KeyName(Keys key, bool friendly) => !friendly ? key.ToString() : key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(CultureInfo.InvariantCulture),
        Keys.Oemplus => "=", Keys.OemMinus => "-", Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.Next => "PageDown", Keys.Prior => "PageUp",
        Keys.Return => "Enter", Keys.Escape => "Esc", Keys.Back => "Backspace", Keys.Capital => "CapsLock",
        _ => key.ToString(),
    };
    public static KeyCombo? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        Keys? key = null;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win": win = true; continue;
            }
            if (key is not null || !Enum.TryParse<Keys>(part, true, out var parsed) || !Enum.IsDefined(parsed) || IsModifier(parsed) || (parsed & Keys.Modifiers) != 0) return null;
            key = parsed;
        }
        return key is { } k ? new KeyCombo(ctrl, alt, shift, win, k) : null;
    }
}

// Runs tiles. Taps arrive with the deck revision the display saw, so an edited deck never runs the wrong tile.
public sealed class ShortcutRunner(Func<ShortcutDeck> deck, Func<string> displayLanguage, Func<bool> away, MediaSessions? media = null)
{
    public async Task<MusicControlResult> ExecuteAsync(DeviceControl command, CancellationToken cancellationToken)
    {
        var (rev, slot) = Protocol.ShortcutTarget(command.Value);
        var current = deck();
        if (current.Revision(displayLanguage()) != rev || current.At(slot) is not { } tile || ShortcutCatalog.Find(tile.Action) is not { } action)
            return new(command.Id, false, "changed");
        if (away() && !action.LockedOk) return new(command.Id, false, "locked");
        bool ok = await RunAsync(tile, media, cancellationToken);
        return new(command.Id, ok, ok ? "" : "failed");
    }

    // Also used by the editor's Try it, without a media session.
    public static async Task<bool> RunAsync(ShortcutTile tile, MediaSessions? media, CancellationToken cancellationToken)
    {
        try
        {
            switch (tile.Action)
            {
                case "mute_mic": return EndpointAudio.ToggleMicrophone();
                case "mute_sound": return EndpointAudio.ToggleSpeakers();
                case "volume_down": return EndpointAudio.Step(-0.04f);
                case "volume_up": return EndpointAudio.Step(0.04f);
                case "play_pause" or "next" or "previous":
                    // The Media app's own player when Windows knows one, otherwise the media key that any player hears.
                    if (media is not null && (await media.ExecuteAsync(new DeviceControl(1, tile.Action), cancellationToken)).Ok) return true;
                    return NativeInput.Press(new KeyCombo(false, false, false, false,
                        tile.Action == "play_pause" ? Keys.MediaPlayPause : tile.Action == "next" ? Keys.MediaNextTrack : Keys.MediaPreviousTrack));
                case "lock": return NativeInput.LockWorkStation();
                case "screens_off": return NativeInput.ScreensOff();
                case "show_desktop": return NativeInput.Press(new KeyCombo(false, false, false, true, Keys.D));
                case "screenshot": return Open("ms-screenclip:");
                case "task_manager": return Open("taskmgr.exe");
                case "open_app": return File.Exists(tile.Target) && Open(tile.Target);
                case "open_folder": return Directory.Exists(tile.Target) && Open(tile.Target);
                case "open_website": return ShortcutDeck.IsWebAddress(tile.Target) && Open(new Uri(tile.Target).AbsoluteUri);
                case "keys": return KeyCombo.Parse(tile.Keys) is { } combo && NativeInput.Press(combo);
                default: return false;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Write($"Shortcut {tile.Action} failed: {error.GetType().Name}");
            return false;
        }
    }
    private static bool Open(string target)
    {
        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return true;
    }

    // Which toggle tiles are lit: the mute state Windows reports right now.
    public static int OnMask(ShortcutDeck deck)
    {
        int mask = 0;
        bool? mic = null, speakers = null;
        foreach (var tile in deck.Tiles)
        {
            bool lit = tile.Action switch
            {
                "mute_mic" => mic ??= EndpointAudio.MicrophoneMuted(),
                "mute_sound" => speakers ??= EndpointAudio.SpeakersMuted(),
                _ => false,
            };
            if (lit) mask |= 1 << tile.Slot;
        }
        return mask;
    }
}
