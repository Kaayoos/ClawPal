using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClawPal.Companion;

public sealed record FoundPrinter(PrinterKind Kind, string Address, string Name, string Serial = "", string Model = "")
{
    public override string ToString() => (Name.Length > 0 ? Name : Address) + (Model.Length > 0 ? " (" + Model + ")" : "") + " · " + Address;
}

// Finds printers without talking to them: Bambu printers announce themselves with SSDP NOTIFY
// messages on UDP 2021, and Klipper (Moonraker), OctoPrint and PrusaLink can announce over mDNS.
// The only packets sent are an SSDP M-SEARCH and an mDNS question, both read-only queries.
public static class PrinterDiscovery
{
    public const int BambuPort = 2021;
    const string BambuTarget = "urn:bambulab-com:device:3dprinter:1";
    static readonly (string Service, PrinterKind Kind)[] Services =
        [("_moonraker._tcp.local", PrinterKind.Moonraker), ("_octoprint._tcp.local", PrinterKind.OctoPrint), ("_prusa-link._tcp.local", PrinterKind.PrusaLink)];

    // Bambu's model codes, as the printers announce them.
    public static string BambuModel(string code) => code switch
    {
        "BL-P001" => "X1 Carbon", "BL-P002" => "X1", "C13" => "X1E", "C11" => "P1P", "C12" => "P1S",
        "N2S" => "A1", "N1" => "A1 mini", "O1D" => "H2D", _ => code
    };

    // One SSDP NOTIFY or M-SEARCH reply from a Bambu printer, or null for anything else.
    public static FoundPrinter? ParseSsdp(string message, IPAddress sender)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in message.Split('\n').Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        bool bambu = (headers.TryGetValue("NT", out var nt) && nt == BambuTarget) || (headers.TryGetValue("ST", out var st) && st == BambuTarget);
        if (!bambu || !headers.TryGetValue("USN", out var serial) || !BambuReadOnly.ValidSerial(serial)) return null;
        // The announced address must be the sender's own, on the local network.
        string address = headers.TryGetValue("Location", out var location) && IPAddress.TryParse(location, out var ip) ? ip.ToString() : sender.ToString();
        if (address != sender.ToString() || !Protocol.IsLanAddress(sender)) return null;
        string name = headers.TryGetValue("DevName.bambu.com", out var n) ? MusicContract.LimitText(n, PrinterContract.MaxNameBytes) : "";
        string model = headers.TryGetValue("DevModel.bambu.com", out var m) ? BambuModel(m) : "";
        return new(PrinterKind.Bambu, address, name.Length > 0 ? name : model.Length > 0 ? "Bambu " + model : "Bambu Lab", serial, model);
    }

    // DNS names with compression pointers; null when malformed.
    static string? ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        int position = offset, jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (position >= packet.Length) return null;
            int length = packet[position];
            if (length == 0) { position++; break; }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length || ++jumps > 16) return null;
                if (!jumped) offset = position + 2;
                position = ((length & 0x3F) << 8) | packet[position + 1];
                jumped = true;
                continue;
            }
            if (position + 1 + length > packet.Length) return null;
            labels.Add(Encoding.UTF8.GetString(packet, position + 1, length));
            position += 1 + length;
        }
        if (!jumped) offset = position;
        return string.Join('.', labels);
    }

    // An mDNS answer for one of the printer services: the instance name and its port, at the sender's address.
    public static List<FoundPrinter> ParseMdns(byte[] packet, IPAddress sender)
    {
        var found = new List<FoundPrinter>();
        if (packet.Length < 12 || !Protocol.IsLanAddress(sender)) return found;
        int questions = packet[4] << 8 | packet[5], records = (packet[6] << 8 | packet[7]) + (packet[8] << 8 | packet[9]) + (packet[10] << 8 | packet[11]);
        int offset = 12;
        for (int i = 0; i < questions; ++i) { if (ReadName(packet, ref offset) is null) return found; offset += 4; }
        var instances = new Dictionary<string, PrinterKind>(StringComparer.OrdinalIgnoreCase);
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < records && offset < packet.Length; ++i)
        {
            string? name = ReadName(packet, ref offset);
            if (name is null || offset + 10 > packet.Length) return found;
            int type = packet[offset] << 8 | packet[offset + 1], length = packet[offset + 8] << 8 | packet[offset + 9];
            int data = offset + 10;
            if (data + length > packet.Length) return found;
            if (type == 12) // PTR: service -> instance
            {
                int at = data;
                string? instance = ReadName(packet, ref at);
                var service = Services.FirstOrDefault(s => string.Equals(s.Service, name, StringComparison.OrdinalIgnoreCase));
                if (instance is not null && service.Service is not null) instances[instance] = service.Kind;
            }
            else if (type == 33 && length >= 6) ports[name] = packet[data + 4] << 8 | packet[data + 5]; // SRV: priority, weight, port
            offset = data + length;
        }
        foreach (var (instance, kind) in instances)
        {
            int port = ports.TryGetValue(instance, out var p) ? p : 80;
            string label = instance.Split("._", 2)[0];
            found.Add(new(kind, port == 80 ? sender.ToString() : sender + ":" + port, MusicContract.LimitText(label, PrinterContract.MaxNameBytes)));
        }
        return found;
    }

    static byte[] MdnsQuestion()
    {
        var packet = new List<byte> { 0, 0, 0, 0, 0, (byte)Services.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var (service, _) in Services)
        {
            foreach (var label in service.Split('.')) { var bytes = Encoding.ASCII.GetBytes(label); packet.Add((byte)bytes.Length); packet.AddRange(bytes); }
            packet.Add(0);
            packet.AddRange(new byte[] { 0, 12, 0x80, 1 }); // PTR, IN with the unicast-response bit
        }
        return packet.ToArray();
    }

    static async Task Listen(UdpClient socket, Action<UdpReceiveResult> received, CancellationToken token)
    {
        try { while (true) received(await socket.ReceiveAsync(token)); }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    // Listens for a few seconds and returns what answered, each printer once.
    public static async Task<List<FoundPrinter>> FindAsync(TimeSpan duration, CancellationToken token)
    {
        var found = new Dictionary<string, FoundPrinter>();
        void Add(FoundPrinter printer) { lock (found) found.TryAdd(printer.Kind + "|" + (printer.Serial.Length > 0 ? printer.Serial : printer.Address), printer); }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(duration);
        var listeners = new List<Task>();
        var sockets = new List<UdpClient>();
        try
        {
            // Bambu announcements; Bambu Studio may hold the same port, so share it.
            try
            {
                var bambu = new UdpClient { ExclusiveAddressUse = false, EnableBroadcast = true };
                bambu.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                bambu.Client.Bind(new IPEndPoint(IPAddress.Any, BambuPort));
                sockets.Add(bambu);
                listeners.Add(Listen(bambu, r => { if (ParseSsdp(Encoding.UTF8.GetString(r.Buffer), r.RemoteEndPoint.Address) is { } p) Add(p); }, stop.Token));
            }
            catch (SocketException) { }
            var search = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            sockets.Add(search);
            listeners.Add(Listen(search, r =>
            {
                if (r.RemoteEndPoint.Port == 5353) foreach (var p in ParseMdns(r.Buffer, r.RemoteEndPoint.Address)) Add(p);
                else if (ParseSsdp(Encoding.UTF8.GetString(r.Buffer), r.RemoteEndPoint.Address) is { } p) Add(p);
            }, stop.Token));
            byte[] msearch = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: {BambuTarget}\r\n\r\n");
            byte[] mdns = MdnsQuestion();
            for (int round = 0; round < 2 && !stop.IsCancellationRequested; ++round)
            {
                foreach (var target in new[] { new IPEndPoint(IPAddress.Broadcast, BambuPort), new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1990) })
                    try { await search.SendAsync(msearch, target, stop.Token); } catch (SocketException) { }
                try { await search.SendAsync(mdns, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), stop.Token); } catch (SocketException) { }
                try { await Task.Delay(TimeSpan.FromSeconds(2), stop.Token); } catch (OperationCanceledException) { }
            }
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
            await Task.WhenAll(listeners);
        }
        lock (found) return found.Values.OrderBy(p => p.Kind).ThenBy(p => p.Name).ToList();
    }
}
