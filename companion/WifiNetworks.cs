using System.Runtime.InteropServices;
using System.Text;

namespace ClawPal.Companion;

/* A nearby network as the PC's Wi-Fi adapter sees it. The display only joins 2.4 GHz networks. */
internal sealed record WifiNetwork(string Ssid, int Quality, bool Has24GHz, bool Secured, bool Connected)
{
    internal string Signal => Quality >= 70 ? L.Text("wifi_signal_strong") : Quality >= 40 ? L.Text("wifi_signal_good") : L.Text("wifi_signal_weak");
    public override string ToString() =>
        Ssid + " · " + (!Has24GHz ? L.Text("wifi_5ghz_only") : Connected ? L.Text("wifi_this_pc_uses_it") : Signal) + (Secured || !Has24GHz ? "" : " · " + L.Text("wifi_open"));
}

internal sealed record WifiScan(WifiNetwork[] Networks, string? Problem, bool NeedsLocation = false);

/* Lists networks through the Windows WLAN service. Nothing is joined or changed, and saved
   passwords are never read: the user types the password. */
internal static class WifiNetworks
{
    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr handle, ref Guid adapter, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid adapter, IntPtr ssid, int bssType, bool secured, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid adapter, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    private const int InterfaceInfoSize = 532, BssEntrySize = 360, AnyBss = 3, CurrentConnection = 7, AccessDenied = 5;

    /* Asks the adapter for a fresh scan, then reads what it found. */
    internal static async Task<WifiScan> ScanAsync(CancellationToken token)
    {
        try { await Task.Run(() => Use((handle, adapters) => { foreach (var adapter in adapters) { var id = adapter; WlanScan(handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } return 0; }), token); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or IOException) { return new([], L.Text("wifi_scan_unavailable")); }
        await Task.Delay(TimeSpan.FromSeconds(3.5), token);
        return await Task.Run(Read, token);
    }

    internal static WifiScan Read()
    {
        try
        {
            var found = new Dictionary<string, WifiNetwork>();
            bool denied = false;
            int adapterCount = Use((handle, adapters) =>
            {
                foreach (var adapter in adapters)
                {
                    var id = adapter;
                    string? current = Connected(handle, id);
                    int result = WlanGetNetworkBssList(handle, ref id, IntPtr.Zero, AnyBss, false, IntPtr.Zero, out var list);
                    if (result == AccessDenied) { denied = true; continue; }
                    if (result != 0) continue;
                    try
                    {
                        int count = Marshal.ReadInt32(list, 4);
                        for (int i = 0; i < count; i++)
                        {
                            var entry = list + 8 + i * BssEntrySize;
                            int length = Math.Min(32, Marshal.ReadInt32(entry));
                            if (length == 0) continue;
                            var bytes = new byte[length]; Marshal.Copy(entry + 4, bytes, 0, length);
                            string ssid = Encoding.UTF8.GetString(bytes);
                            if (ssid.Trim('\0').Length == 0) continue;
                            int quality = Marshal.ReadInt32(entry, 60);
                            bool secured = (Marshal.ReadInt16(entry, 88) & 0x10) != 0;
                            bool is24 = Marshal.ReadInt32(entry, 92) is > 2_000_000 and < 3_000_000;
                            found.TryGetValue(ssid, out var known);
                            found[ssid] = new(ssid, Math.Max(known?.Quality ?? 0, quality), (known?.Has24GHz ?? false) || is24, (known?.Secured ?? false) || secured, ssid == current);
                        }
                    }
                    finally { WlanFreeMemory(list); }
                }
                return adapters.Length;
            });
            var networks = found.Values.OrderByDescending(n => n.Connected && n.Has24GHz).ThenByDescending(n => n.Has24GHz).ThenByDescending(n => n.Quality).ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase).ToArray();
            // Windows hides nearby networks from apps that may not use location.
            string? problem = adapterCount == 0 ? L.Text("wifi_no_adapter") : networks.Length == 0 ? (denied ? L.Text("wifi_needs_location") : L.Text("wifi_none_found")) : null;
            return new(networks, problem, networks.Length == 0 && denied);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or IOException) { return new([], L.Text("wifi_scan_unavailable")); }
    }

    private static string? Connected(IntPtr handle, Guid adapter)
    {
        if (WlanQueryInterface(handle, ref adapter, CurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) return null;
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile name (256 characters), then the association's SSID.
            if (Marshal.ReadInt32(data) != 1) return null;
            int length = Math.Min(32, Marshal.ReadInt32(data, 520));
            var bytes = new byte[length]; Marshal.Copy(data + 524, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { WlanFreeMemory(data); }
    }

    private static int Use(Func<IntPtr, Guid[], int> action)
    {
        int opened = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (opened != 0) throw new IOException(L.Text("wifi_scan_unavailable"));
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return action(handle, []);
            Guid[] adapters;
            try
            {
                int count = Marshal.ReadInt32(list);
                adapters = Enumerable.Range(0, count).Select(i => Marshal.PtrToStructure<Guid>(list + 8 + i * InterfaceInfoSize)).ToArray();
            }
            finally { WlanFreeMemory(list); }
            return action(handle, adapters);
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }
}

/* Nearby networks above the name field: picking one fills the name, and the password stays typed by hand. */
internal sealed class WifiPicker : ClawStack
{
    private readonly ChoiceBox networks = new();
    private readonly ClawButton rescan = new() { Text = L.Text("wifi_scan_again"), Height = 44 };
    private readonly Label note = ClawTheme.Label(L.Text("wifi_scanning"), 400, 9, false, 20);
    private readonly ClawButton location = new() { Text = L.Text("wifi_open_location_settings"), Height = 38, Visible = false };
    private readonly ClawTextField ssid;
    private readonly bool preview;
    private bool scanning, filling;
    internal ChoiceBox Networks => networks;
    internal WifiPicker(ClawTextField ssid, bool preview)
    {
        this.ssid = ssid; this.preview = preview; Gap = 6;
        var row = new ClawRow(); row.Add(networks); row.Add(rescan, 170);
        var locationRow = new ClawRow(); locationRow.Add(location, 250);
        Add(ClawTheme.Label(L.Text("wifi_nearby"), 400, 10, true, 24)); Add(row); Add(note); Add(locationRow);
        // Windows 11 lists nearby networks only to apps allowed to use location.
        location.Click += (_, _) => { if (!preview) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); };
        networks.Text = L.Text("wifi_scanning");
        networks.SelectedIndexChanged += (_, _) =>
        {
            if (filling || networks.SelectedItem is not WifiNetwork network) return;
            ssid.Text = network.Ssid; Describe(network);
        };
        rescan.Click += async (_, _) => await LoadAsync(true);
    }
    private void Describe(WifiNetwork network)
    {
        note.Text = !network.Has24GHz ? L.Text("wifi_picked_5ghz") : !network.Secured ? L.Text("wifi_picked_open") : L.Text("wifi_pick_hint");
        note.ForeColor = network.Has24GHz ? ClawTheme.Muted : ClawTheme.Warning;
    }
    internal async Task LoadAsync(bool fresh = false)
    {
        if (scanning || IsDisposed) return;
        scanning = true; rescan.Enabled = false;
        try
        {
            if (preview) { Fill(new([new("Home Wi-Fi", 90, true, true, true), new("Home Wi-Fi 5G", 82, false, true, false), new("Garden", 38, true, true, false)], null)); return; }
            // Windows' last scan shows at once; a fresh one replaces it a few seconds later.
            if (!fresh) Fill(await Task.Run(WifiNetworks.Read));
            if (IsDisposed) return;
            note.Text = L.Text("wifi_scanning"); note.ForeColor = ClawTheme.Muted;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Fill(await WifiNetworks.ScanAsync(timeout.Token));
        }
        catch (OperationCanceledException) { }
        finally { scanning = false; if (!rescan.IsDisposed) rescan.Enabled = true; }
    }
    private void Fill(WifiScan scan)
    {
        if (IsDisposed) return;
        filling = true;
        try
        {
            networks.Items.Clear(); networks.Items.AddRange(scan.Networks);
            var match = scan.Networks.FirstOrDefault(n => n.Ssid == ssid.Text.Trim());
            // An empty name takes the network this PC is on, when the display can join it.
            var pick = match ?? (ssid.Text.Trim().Length == 0 ? scan.Networks.FirstOrDefault(n => n.Connected && n.Has24GHz) : null);
            networks.SelectedIndex = pick is null ? -1 : networks.Items.IndexOf(pick);
            if (pick is null) networks.Text = scan.Networks.Length == 0 ? L.Text("wifi_no_networks") : L.Text("wifi_choose_network");
            if (pick is not null && match is null) ssid.Text = pick.Ssid;
        }
        finally { filling = false; }
        location.Visible = scan.NeedsLocation;
        if (networks.SelectedItem is WifiNetwork chosen) Describe(chosen);
        else { note.Text = scan.Problem ?? L.Text("wifi_pick_hint"); note.ForeColor = ClawTheme.Muted; }
    }
}
