using ClawPal.Companion;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

internal static class ShortcutChecks
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr w, IntPtr l);
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
        void Reject(ShortcutDeck deck, string name) { bool rejected = false; try { deck.Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected, name); }

        var starter = ShortcutDeck.Starter("en");
        starter.Validate();
        Check(starter.Size == 8 && starter.Tiles.Length == 8 && starter.At(0)?.Action == "mute_mic" && starter.At(6)?.Action == "lock", "the starter deck fills an 8-tile grid with safe tiles");
        Check(ShortcutDeck.Starter("pl").At(0)?.Label == L.DisplayText("pl", "mute_mic") && starter.At(0)?.Label == "Mute mic", "starter labels follow the display's language");
        Check(ShortcutCatalog.Actions.All(a => ShortcutArt.FindIcon(a.Icon) is not null && ShortcutArt.HasColor(a.Color)), "every action starts with an icon and colour the display knows");
        Check(ShortcutCatalog.Actions.All(a => L.Text($"shortcut_{a.Id}") != $"shortcut_{a.Id}") && ShortcutArt.Colors.All(c => L.Text($"colour_{c.Id}") != $"colour_{c.Id}"), "every action and colour has a name in the PC app");

        // What the display learns: looks only, never what a tile runs.
        var app = ShortcutDeck.NewTile(8, "open_app", "en", @"C:\Tools\Secret Notes.exe");
        var site = ShortcutDeck.NewTile(9, "open_website", "en", "https://www.example.com/private?token=abc");
        var keys = ShortcutDeck.NewTile(10, "keys", "en", keys: "Ctrl+Shift+M");
        var deck = starter.Resize(12).Set(8, app).Set(9, site).Set(10, keys);
        deck.Validate();
        Check(app.Label == "Secret Notes" && app.UsesAppIcon && site.Label == "example.com" && keys.Label == "Ctrl + Shift + M", "new tiles are named after their app, site or keys");
        string sent = System.Text.Encoding.UTF8.GetString(deck.DisplayJson("en"));
        Check(!sent.Contains("Tools") && !sent.Contains("token") && !sent.Contains("Shift+M") && !sent.Contains("open_app") && sent.Contains("\"app_icon\":true"),
            "the display receives labels, icons and colours but no paths, addresses, keys or actions");
        using (var json = JsonDocument.Parse(sent))
        {
            var root = json.RootElement; var mic = root.GetProperty("tiles")[0];
            Check(root.GetProperty("rev").GetInt32() == deck.Revision("en") && root.GetProperty("size").GetInt32() == 12 && root.GetProperty("tiles").GetArrayLength() == 11,
                "the deck names its revision and size");
            Check(mic.GetProperty("toggle").GetBoolean() && mic.GetProperty("locked_ok").GetBoolean() && mic.GetProperty("label_on").GetString() == "Mic muted" && mic.GetProperty("icon").GetString() == "mic",
                "mute tiles light up and keep working while the PC is locked");
            Check(!root.GetProperty("tiles")[6].TryGetProperty("toggle", out _) && root.GetProperty("tiles")[2].GetProperty("repeat").GetBoolean(), "only volume tiles repeat while held");
            Check(System.Text.Encoding.UTF8.GetByteCount(sent) < 4096, "a full deck stays well under the display's 4 KB limit");
        }
        int rev = deck.Revision("en");
        Check(rev is >= 1 and <= Protocol.ShortcutMaxRev && rev == deck.Revision("en"), "the revision is stable and fits the tap's 16 bits");
        Check(deck.Set(8, app with { Target = @"C:\Tools\Other.exe" }).Revision("en") != rev && deck.Set(0, deck.At(0)! with { Color = "blue" }).Revision("en") != rev,
            "a new target or look gives the deck a new revision");

        // Moving, resizing and validation.
        var moved = deck.Move(0, 11);
        Check(moved.At(11)?.Action == "mute_mic" && moved.At(0) is null && deck.Move(0, 1).At(0)?.Action == "mute_sound" && deck.Move(0, 1).At(1)?.Action == "mute_mic", "dragging moves onto empty slots and swaps with used ones");
        var small = starter.Resize(16).Resize(8);
        Check(small.Size == 8 && small.Tiles.Length == 8 && small.Tiles.All(t => t.Slot < 8), "a smaller grid keeps every tile when there is room");
        bool refused = false; try { deck.Resize(6); } catch (ArgumentException) { refused = true; }
        Check(refused, "a grid too small for its tiles is refused");
        Reject(deck with { Size = 10 }, "grids come in 6, 8, 12 or 16 slots");
        Reject(deck.Set(3, deck.At(3)! with { Icon = "rocket" }), "unknown icons are rejected");
        Reject(deck.Set(3, deck.At(3)! with { Label = new string('a', 25) }), "labels longer than 24 characters are rejected");
        Reject(deck.Set(3, deck.At(3)! with { Label = "日本" }), "labels the display cannot draw are rejected");
        Reject(deck.Set(9, site with { Target = "file:///C:/Windows" }), "websites must use http or https");
        Reject(deck.Set(9, site with { Target = "javascript:alert(1)" }), "script addresses are not websites");
        Reject(deck.Set(8, app with { Target = "notes.exe" }), "apps need a full path");
        Reject(deck.Set(10, keys with { Keys = "Ctrl+Shift" }), "a key combination needs a key besides its modifiers");
        Check(ShortcutDeck.Fit("Łódź 日本 — weekly sync with the whole team") == "Łódź  — weekly sync with", "automatic labels drop characters the display cannot draw and stop at 24");
        Check(KeyCombo.Parse("ctrl+alt+F13")?.Value == "Ctrl+Alt+F13" && KeyCombo.Parse("Win+D")?.Text == "Win + D" && KeyCombo.Parse("Ctrl+D1")?.Text == "Ctrl + 1" && KeyCombo.Parse("Ctrl+Shift+M+K") is null,
            "key combinations read and write as text");

        // The runner answers only for the deck the display saw, and respects a locked PC.
        bool locked = false;
        var runner = new ShortcutRunner(() => deck, () => "en", () => locked);
        var stale = runner.ExecuteAsync(new DeviceControl(1, "shortcut", Protocol.ShortcutValue(rev % Protocol.ShortcutMaxRev + 1, 0)), CancellationToken.None).GetAwaiter().GetResult();
        var empty = runner.ExecuteAsync(new DeviceControl(2, "shortcut", Protocol.ShortcutValue(rev, 11)), CancellationToken.None).GetAwaiter().GetResult();
        locked = true;
        var blocked = runner.ExecuteAsync(new DeviceControl(3, "shortcut", Protocol.ShortcutValue(rev, 7)), CancellationToken.None).GetAwaiter().GetResult();
        Check(stale == new MusicControlResult(1, false, "changed") && empty == new MusicControlResult(2, false, "changed") && blocked == new MusicControlResult(3, false, "locked"),
            "an edited deck, an empty slot and a locked PC each refuse the tap without running anything");
        var queue = new MusicControlQueue();
        Check(queue.Enqueue(new DeviceControl(5, "shortcut", Protocol.ShortcutValue(rev, 7))), "Shortcuts taps share the ordered control queue");

        var config = new Configuration();
        Check(config.Shortcuts is null, "a new configuration fills its deck when loaded");
        var path = Path.Combine(Path.GetTempPath(), "clawpal-shortcut-check.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new Configuration { Shortcuts = deck.Set(3, deck.At(3)! with { Icon = "rocket" }) }));
            var loaded = Configuration.LoadFrom(path, recover: false);
            Check(loaded.Shortcuts?.Tiles.Length == 8 && loaded.Shortcuts.Size == 8, "a saved deck that no longer validates is replaced by the starter deck");
            File.WriteAllText(path, JsonSerializer.Serialize(new Configuration { Shortcuts = deck }));
            Check(Configuration.LoadFrom(path, recover: false).Shortcuts?.Revision("en") == rev, "a saved deck loads unchanged");
        }
        finally { File.Delete(path); }

        // The editor: the preview lays tiles out like the display, and a drag moves one.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { Size = new(700, 520), ShowInTaskbar = false };
                using var preview = new DeckPreview(starter.Resize(12), BoardCatalog.DefaultId, () => "en") { Bounds = new(10, 10, 640, 400) };
                ShortcutDeck? changed = null; preview.Changed += value => changed = value;
                form.Controls.Add(preview); form.Show(); Application.DoEvents();
                var first = preview.TileBounds(0); var last = preview.TileBounds(11);
                Check(preview.Screen.Contains(Rectangle.Round(first)) && preview.Screen.Contains(Rectangle.Round(last)) && last.X > first.X && last.Y > first.Y, "the preview places a 4 x 3 grid inside the display");
                static IntPtr Point(PointF p) => new(((int)p.Y << 16) | (int)p.X);
                PointF Centre(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
                SendMessage(preview.Handle, 0x201, new IntPtr(1), Point(Centre(first)));
                SendMessage(preview.Handle, 0x200, new IntPtr(1), Point(Centre(preview.TileBounds(5))));
                SendMessage(preview.Handle, 0x200, new IntPtr(1), Point(Centre(last)));
                SendMessage(preview.Handle, 0x202, IntPtr.Zero, Point(Centre(last))); Application.DoEvents();
                Check(changed is { } dragged && dragged.At(11)?.Action == "mute_mic" && dragged.At(0) is null && preview.Selected == 11, "dragging a tile in the preview moves it and keeps it selected");
                preview.Focus(); SendMessage(preview.Handle, 0x100, new IntPtr((int)Keys.Delete), IntPtr.Zero); Application.DoEvents();
                Check(changed is { } removed && removed.At(11) is null && removed.Tiles.Length == 7, "Delete removes the selected tile");
                form.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
        return passed;
    }
}
