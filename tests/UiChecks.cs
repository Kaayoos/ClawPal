using ClawPal.Companion;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static class UiChecks
{
    private static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    internal static int Run()
    {
        int passed = 0; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
                using var connection = new Connection(new Configuration());

                using (var setup = new SetupForm(new Configuration(), connection, true) { ShowInTaskbar = false })
                {
                    setup.Show(); Application.DoEvents();
                    var steps = Descendants(setup).OfType<StepList>().Single(); var crab = Descendants(setup).OfType<CrabView>().Single();
                    var version = Descendants(setup).OfType<Label>().Single(l => l.Parent == steps.Parent && l.Text.StartsWith("ClawPal 0", StringComparison.Ordinal));
                    foreach (var size in new[] { new Size(960, 700), new Size(1040, 760), new Size(1500, 960) })
                    {
                        setup.ClientSize = size; Application.DoEvents();
                        Check(!crab.Visible || (!crab.Bounds.IntersectsWith(steps.Bounds) && crab.Bottom <= version.Top), $"Setup's crab stays clear of the steps and version at {size.Width}x{size.Height}");
                    }
                    ClawTheme.Use("studio", "blue"); Application.DoEvents();
                    Check(!crab.Visible, "Studio setup has no crab beside the steps");
                    ClawTheme.Use(Themes.Default, Themes.DefaultAccent); Application.DoEvents();
                    Check(crab.Visible, "Crab setup brings the crab back");

                    var stepField = typeof(SetupForm).GetField("step", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    void Go(int index) { stepField.SetValue(setup, Enum.ToObject(stepField.FieldType, index)); typeof(SetupForm).GetMethod("ShowStep", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(setup, null); Application.DoEvents(); }
                    Go(1);
                    var back = Field<ClawButton>(setup, "back"); var next = Field<ClawButton>(setup, "next"); var status = Field<Label>(setup, "status");
                    Check(back.Visible && !back.Bounds.IntersectsWith(status.Bounds) && !back.Bounds.IntersectsWith(next.Bounds), "Back stays visible and clear of the status line on later steps");

                    Go(2);
                    var picker = Descendants(setup).OfType<WifiPicker>().Single(); var ssid = Field<ClawTextField>(setup, "ssid");
                    Check(picker.Networks.Items.Count == 3 && ssid.Text == "Home Wi-Fi", "The Wi-Fi list fills the name with the network this PC uses");
                    picker.Networks.SelectedIndex = picker.Networks.Items.Cast<WifiNetwork>().ToList().FindIndex(n => !n.Has24GHz); Application.DoEvents();
                    Check(ssid.Text == "Home Wi-Fi 5G" && Descendants(picker).OfType<Label>().Any(l => l.ForeColor == ClawTheme.Warning), "Picking a 5 GHz-only network fills its name and warns that the display can't join it");
                    Check(new WifiNetwork("Cafe", 50, true, false, false).ToString().Contains(L.Text("wifi_open")) && new WifiNetwork("Office", 90, false, true, false).ToString().Contains(L.Text("wifi_5ghz_only")), "Network entries say when one is open or 5 GHz only");
                    ssid.Text = "Typed by hand"; picker.Networks.Items.Clear(); Check(ssid.Text == "Typed by hand", "A typed network name stays when the list changes");
                    setup.Close();
                }

                var settings = new DeviceSettings() with { HomeOrder = [0] };
                using (var form = new SettingsForm(new Configuration { DisplaySettings = settings }, connection, true) { ShowInTaskbar = false })
                {
                    form.Show(); Application.DoEvents();
                    void Page(string name) { Descendants(form).OfType<ClawButton>().Single(c => c.Text == name && c.Parent?.Dock == DockStyle.Left).PerformClick(); Application.DoEvents(); }
                    Page("Home");
                    var chips = Descendants(form).OfType<AppChip>().ToArray();
                    Check(chips.Length > 0 && chips.All(c => c.Enabled) && chips.All(c => c.AppId != 0), "Add to Home offers every enabled app that isn't on Home yet");
                    chips.First(c => c.AppId == 2).PerformClick(); Application.DoEvents();
                    var draft = Field<DeviceSettings>(form, "draft");
                    Check(draft.HomeOrder.SequenceEqual(new[] { 0, 2 }) && !Descendants(form).OfType<AppChip>().Any(c => c.AppId == 2), "Clicking an app adds it to Home and removes it from the choices");
                    foreach (int id in new[] { 1, 3, 4 }) { Descendants(form).OfType<AppChip>().First(c => c.AppId == id).PerformClick(); Application.DoEvents(); }
                    Check(Field<DeviceSettings>(form, "draft").HomeOrder.Length == 5 && Descendants(form).OfType<AppChip>().All(c => !c.Enabled), "A full Home turns the remaining choices off");
                    Page("Apps");
                    var switches = Descendants(form).OfType<ClawSwitch>().Select(s => s.Text).ToArray();
                    Check(switches.Contains(L.Text("track_game_fps_in_pc_monitor")) && switches.Contains(L.Text("claude_live_limits")) && switches.Contains(L.Text("automatically_open_spotify_or_codex")), "App options live with their apps on the Apps page");
                    Check(Descendants(form).OfType<ClawButton>().Any(b => b.Text == L.Text("claude_link_button")), "The Claude Code connection is under Claude Code in Apps");
                    Page("PC app");
                    Check(Descendants(form).OfType<ClawSwitch>().Any(s => s.Text == L.Text("start_clawpal_with_windows")) && !Descendants(form).OfType<ClawSwitch>().Any(s => s.Text == L.Text("claude_live_limits")), "The PC app page keeps only settings for the PC app itself");
                    Page("Connection");
                    Check(Descendants(form).OfType<WifiPicker>().Single().Networks.Items.Count == 3, "Changing Wi-Fi also offers nearby networks");
                    form.Close();
                }
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new Exception("UI checks timed out");
        if (failure is not null) throw new Exception("UI checks failed", failure);
        return passed;
    }
}
