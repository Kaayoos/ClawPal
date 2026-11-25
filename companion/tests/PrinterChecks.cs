using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClawPal.Companion;

// The Printer app on the PC: real report shapes from each kind of printer, the merged Bambu state,
// the telemetry the display receives, discovery parsing, and the read-only guarantees. No real
// printer is involved; payloads follow what Bambu, Moonraker, OctoPrint and PrusaLink send.
static class PrinterChecks
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { Check(true, name); return; }
        throw new Exception("FAIL (no " + typeof(T).Name + "): " + name);
    }

    // A P1S answering pushall mid-print: the full report (trimmed of AMS and camera details).
    const string BambuFull = """
        {"print":{"bed_target_temper":55.0,"bed_temper":54.9,"chamber_temper":5,"command":"push_status","fan_gear":0,
        "gcode_file":"/data/Metadata/plate_1.gcode","gcode_file_prepare_percent":"100","gcode_start_time":"1763280000","gcode_state":"RUNNING",
        "heatbreak_fan_speed":"15","hms":[],"home_flag":-1066425961,"layer_num":118,"lifecycle":"product",
        "lights_report":[{"mode":"on","node":"chamber_light"}],"mc_percent":42,"mc_print_line_number":"62391","mc_print_stage":"2",
        "mc_print_sub_stage":0,"mc_remaining_time":103,"nozzle_diameter":"0.4","nozzle_target_temper":220.0,"nozzle_temper":219.6,
        "online":{"ahb":false,"rfid":false,"version":7},"print_error":0,"print_type":"cloud","sdcard":true,"sequence_id":"2021",
        "spd_lvl":2,"spd_mag":100,"stg":[2,14,1],"stg_cur":0,"subtask_name":"Desk organizer","total_layer_num":284,
        "upload":{"file_size":0,"finish_size":0,"message":"Good","progress":0,"status":"idle"},"wifi_signal":"-49dBm"}}
        """;

    public static async Task<int> RunAsync()
    {
        Bambu();
        OtherPrinters();
        TelemetryShape();
        Discovery();
        Guards();
        await BrokerConversation();
        await HttpPolling();
        SourceScan();
        return passed;
    }

    static PrintStatus Feed(JsonObject state, string report) { Check(PrinterContract.MergeReport(state, Encoding.UTF8.GetBytes(report)), "bambu: report merges"); return PrinterContract.FromBambu(state); }

    static void Bambu()
    {
        var state = new JsonObject();
        var s = Feed(state, BambuFull);
        Check(s.State == PrintStates.Printing && s.Progress == 42 && s.RemainingS == 103 * 60 && s.Layer == 118 && s.Layers == 284 &&
              s.File == "Desk organizer" && s.Nozzle == 219.6 && s.NozzleTarget == 220 && s.Bed == 54.9 && s.BedTarget == 55 && s.Error == "",
              "bambu: a full report gives progress, time left in seconds, layers, file and temperatures");
        // P1 and A1 then send only what changed.
        s = Feed(state, """{"print":{"bed_temper":55.0,"wifi_signal":"-48dBm","command":"push_status","msg":1,"sequence_id":"2040"}}""");
        Check(s.State == PrintStates.Printing && s.Progress == 42 && s.Bed == 55 && s.File == "Desk organizer" && s.Layers == 284,
              "bambu: a changes-only report keeps every field it does not mention");
        s = Feed(state, """{"print":{"mc_percent":43,"mc_remaining_time":101,"layer_num":121,"command":"push_status","msg":1,"sequence_id":"2050"}}""");
        Check(s.Progress == 43 && s.RemainingS == 101 * 60 && s.Layer == 121 && s.Nozzle == 219.6, "bambu: progress moves with the deltas");
        s = Feed(state, """{"info":{"command":"get_version","module":[]}}""");
        Check(s.Progress == 43, "bambu: reports without print change nothing");
        Check(!PrinterContract.MergeReport(state, Encoding.UTF8.GetBytes("[1,2]")) && !PrinterContract.MergeReport(state, Encoding.UTF8.GetBytes("{bad")),
              "bambu: payloads that are not JSON objects are refused");
        // Nested objects merge field by field; arrays are replaced whole.
        Feed(state, """{"print":{"online":{"rfid":true},"stg":[1]}}""");
        Check(state["print"]!["online"]!["version"]!.GetValue<int>() == 7 && state["print"]!["online"]!["rfid"]!.GetValue<bool>() &&
              state["print"]!["stg"]!.AsArray().Count == 1, "bambu: nested objects merge and arrays are replaced");

        // Filament runs out in the AMS: paused with 0700_8011.
        s = Feed(state, """{"print":{"gcode_state":"PAUSE","print_error":117473297,"mc_percent":57}}""");
        Check(s.State == PrintStates.Paused && s.Reason == PrintStates.Filament && s.Error == "0700_8011" && s.Progress == 57,
              "bambu: a filament runout pause names filament and its code");
        s = Feed(state, """{"print":{"print_error":0,"hms":[{"attr":117448704,"code":131073}]}}""");
        Check(s.Reason == PrintStates.Error && s.Error == "0700_2000_0002_0001", "bambu: a pause with a serious HMS message is an error with its HMS code");
        s = Feed(state, """{"print":{"hms":[{"attr":117448704,"code":262145}]}}""");
        Check(s.Reason == PrintStates.User && s.Error == "", "bambu: a pause with only an informational HMS message counts as someone's own pause");
        s = Feed(state, """{"print":{"gcode_state":"RUNNING","hms":[]}}""");
        Check(s.State == PrintStates.Printing && s.Reason == "" && s.Error == "", "bambu: resuming clears the reason");
        s = Feed(state, """{"print":{"gcode_state":"FAILED","print_error":50348044}}""");
        Check(s.State == PrintStates.Failed && s.Reason == PrintStates.User && s.Error == "", "bambu: a cancelled task (0300_400C) is someone's own stop");
        s = Feed(state, """{"print":{"gcode_state":"FAILED","print_error":50364437}}""");
        Check(s.State == PrintStates.Failed && s.Reason == PrintStates.Error && s.Error == "0300_8015", "bambu: any other failure is an error with its code");
        s = Feed(state, """{"print":{"gcode_state":"FINISH","print_error":0,"mc_percent":100,"mc_remaining_time":0,"layer_num":284}}""");
        Check(s.State == PrintStates.Finished && s.Progress == 100 && s.RemainingS == 0 && s.Error == "", "bambu: a finished print is full with no time left");
        s = Feed(state, """{"print":{"gcode_state":"IDLE"}}""");
        Check(s.State == PrintStates.Idle && s.File == "" && s.Progress == 0 && s.Nozzle == 219.6, "bambu: idle keeps temperatures but drops the job");
        foreach (var (gcode, expected) in new[] { ("PREPARE", PrintStates.Preparing), ("SLICING", PrintStates.Preparing), ("INIT", PrintStates.Preparing), ("RUNNING", PrintStates.Printing), ("UNKNOWN_STATE", PrintStates.Idle) })
            Check(Feed(new JsonObject(), "{\"print\":{\"gcode_state\":\"" + gcode + "\"}}").State == expected, "bambu: gcode_state " + gcode + " maps to " + expected);
        // H2 printers pack each extruder's temperature as target << 16 | current.
        s = Feed(new JsonObject(), """{"print":{"gcode_state":"RUNNING","device":{"extruder":{"state":16,"info":[{"id":0,"temp":9830400},{"id":1,"temp":14418139}]}}}}""");
        Check(s.Nozzle == 219 && s.NozzleTarget == 220, "bambu: the active H2 nozzle's packed temperature is read");
        s = Feed(new JsonObject(), """{"print":{"gcode_state":"RUNNING","gcode_file":"/sdcard/cache/Benchy.gcode.3mf","mc_percent":"7"}}""");
        Check(s.File == "Benchy.gcode" && s.Progress == 7, "bambu: without a task name the file name is used, and numbers may come as strings");
    }

    static void OtherPrinters()
    {
        using var moon = JsonDocument.Parse("""
            {"result":{"eventtime":1234.5,"status":{"print_stats":{"filename":"cubes/voron_cube.gcode","total_duration":1300.2,"print_duration":1200.0,
            "filament_used":1234.5,"state":"printing","message":"","info":{"total_layer":150,"current_layer":60}},
            "display_status":{"progress":0.4,"message":null},"virtual_sdcard":{"progress":0.41,"is_active":true},
            "extruder":{"temperature":239.8,"target":240.0,"power":0.5},"heater_bed":{"temperature":109.9,"target":110.0,"power":0.6}}}}
            """);
        var s = PrinterContract.FromMoonraker(moon.RootElement);
        Check(s.State == PrintStates.Printing && s.Progress == 40 && s.RemainingS == 1800 && s.Layer == 60 && s.Layers == 150 && s.File == "voron_cube" &&
              s.Nozzle == 239.8 && s.BedTarget == 110, "moonraker: printing state, progress, estimated time left, layers and temperatures");
        foreach (var (klipper, state, reason) in new[] { ("standby", PrintStates.Idle, ""), ("paused", PrintStates.Paused, ""), ("complete", PrintStates.Finished, ""),
                     ("cancelled", PrintStates.Failed, PrintStates.User), ("error", PrintStates.Failed, PrintStates.Error) })
        {
            using var doc = JsonDocument.Parse("{\"result\":{\"status\":{\"print_stats\":{\"state\":\"" + klipper + "\",\"filename\":\"a.gcode\",\"message\":\"Heater extruder not heating at expected rate\"}}}}");
            var m = PrinterContract.FromMoonraker(doc.RootElement);
            Check(m.State == state && m.Reason == reason && (klipper != "error" || m.Error.StartsWith("Heater")), "moonraker: " + klipper + " maps to " + state);
        }

        using var job = JsonDocument.Parse("""{"job":{"file":{"name":"cube.gcode","origin":"local"},"estimatedPrintTime":3600},"progress":{"completion":25.5,"filepos":100,"printTime":900,"printTimeLeft":2700},"state":"Printing"}""");
        using var printer = JsonDocument.Parse("""{"temperature":{"tool0":{"actual":214.8,"target":215.0},"bed":{"actual":60.1,"target":60.0}},"state":{"text":"Printing","flags":{"operational":true,"paused":false,"printing":true,"cancelling":false,"pausing":false,"error":false,"ready":false,"closedOrError":false}}}""");
        s = PrinterContract.FromOctoPrint(job.RootElement, printer.RootElement);
        Check(s.State == PrintStates.Printing && s.Progress == 25 && s.RemainingS == 2700 && s.File == "cube" && s.Nozzle == 214.8 && s.Bed == 60.1,
              "octoprint: printing state, progress, time left and temperatures");
        Check(PrinterContract.FromOctoPrint(job.RootElement, null).State == PrintStates.Offline, "octoprint: no printer connected to OctoPrint is offline");
        using var done = JsonDocument.Parse("""{"job":{"file":{"name":"cube.gcode"}},"progress":{"completion":100.0,"printTimeLeft":0},"state":"Operational"}""");
        using var operational = JsonDocument.Parse("""{"temperature":{"tool0":{"actual":40,"target":0},"bed":{"actual":35,"target":0}},"state":{"text":"Operational","flags":{"operational":true,"ready":true}}}""");
        Check(PrinterContract.FromOctoPrint(done.RootElement, operational.RootElement).State == PrintStates.Finished, "octoprint: a job at 100% while operational is finished");
        using var error = JsonDocument.Parse("""{"temperature":{},"state":{"text":"Error","flags":{"operational":false,"error":true,"closedOrError":true}}}""");
        Check(PrinterContract.FromOctoPrint(job.RootElement, error.RootElement) is { State: PrintStates.Failed, Reason: PrintStates.Error }, "octoprint: an error is a failure");

        using var status = JsonDocument.Parse("""{"job":{"id":12,"progress":63.0,"time_remaining":1500,"time_printing":2400},"printer":{"state":"PRINTING","temp_bed":59.9,"target_bed":60.0,"temp_nozzle":214.7,"target_nozzle":215.0,"axis_z":12.4}}""");
        using var prusaJob = JsonDocument.Parse("""{"id":12,"state":"PRINTING","progress":63.0,"time_remaining":1500,"time_printing":2400,"file":{"name":"BENCHY~1.BGC","display_name":"benchy_0.4n_0.2mm_PLA_MK4_1h.bgcode","path":"/usb"}}""");
        s = PrinterContract.FromPrusaLink(status.RootElement, prusaJob.RootElement);
        Check(s.State == PrintStates.Printing && s.Progress == 63 && s.RemainingS == 1500 && s.File == "benchy_0.4n_0.2mm_PLA_MK4_1h" && s.NozzleTarget == 215 && s.Bed == 59.9,
              "prusalink: printing state, progress, time left, file and temperatures");
        foreach (var (prusa, state) in new[] { ("IDLE", PrintStates.Idle), ("READY", PrintStates.Idle), ("PAUSED", PrintStates.Paused), ("ATTENTION", PrintStates.Paused),
                     ("FINISHED", PrintStates.Finished), ("STOPPED", PrintStates.Failed), ("ERROR", PrintStates.Failed) })
        {
            using var doc = JsonDocument.Parse("{\"printer\":{\"state\":\"" + prusa + "\"}}");
            Check(PrinterContract.FromPrusaLink(doc.RootElement, null).State == state, "prusalink: " + prusa + " maps to " + state);
        }
    }

    static void TelemetryShape()
    {
        var stats = PrinterContract.Telemetry(true, [
            (3, "Voron", new PrintStatus(PrintStates.Idle, Nozzle: 24)),
            (1, "Bambu P1S", new PrintStatus(PrintStates.Printing, Progress: 42, RemainingS: 6180, Layer: 118, Layers: 284, File: "Desk organizer", Nozzle: 219.64, NozzleTarget: 220, Bed: 55, BedTarget: 55)),
            (2, "Ender", PrintStatus.Offline),
            (4, "Mini", new PrintStatus(PrintStates.Finished, Progress: 100)),
            (5, "Fifth", new PrintStatus(PrintStates.Idle))]);
        Check(stats.Printers.Count == 4 && stats.Printers[0].Id == 1 && stats.Printers[1].Id == 4 && stats.Printers[2].Id == 3 && stats.Printers[3].Id == 5,
              "telemetry: at most four printers, the busiest first");
        string json = JsonSerializer.Serialize(new { printer = stats }, Protocol.Json);
        Check(json.Contains("\"remaining_s\":6180") && json.Contains("\"nozzle_target\":220") && json.Contains("\"state\":\"printing\"") && json.Contains("\"nozzle\":219.6,") &&
              !json.Contains("\"reason\"") && !json.Contains("\"error\"") && json.Contains("\"alerts\":true"), "telemetry: snake_case fields, rounded temperatures, empty fields left out");
        var bounded = PrinterContract.Item(300, new string('n', 50), new PrintStatus("melting", "cat", 150, -5, -1, 2_000_000, new string('f', 90), double.NaN, 900, -60, 55, "E\u0007" + new string('e', 40)));
        Check(bounded.Id == 255 && bounded.Name.Length == 31 && bounded.State == PrintStates.Idle && bounded.Reason is null && bounded.Progress == 100 && bounded.RemainingS == 0 &&
              bounded.Layers == 1_000_000 && bounded.File!.Length == 63 && bounded.Nozzle is null && bounded.NozzleTarget is null && bounded.Bed is null && bounded.Error!.Length <= 31 && !bounded.Error.Contains('\u0007'),
              "telemetry: every value is bounded to what the display accepts");
        string text = JsonSerializer.Serialize(PrinterContract.Telemetry(false, Enumerable.Range(1, 4).Select(i => (i, new string('x', 31), new PrintStatus(PrintStates.Paused, PrintStates.Filament, 99, PrinterContract.MaxRemainingS, 999999, 999999, new string('é', 31), 599.9, 599.9, 299.9, 299.9, new string('E', 31))))), Protocol.Json);
        Check(Encoding.UTF8.GetByteCount(text) < 1600, "telemetry: four printers at their longest stay well inside the frame (" + Encoding.UTF8.GetByteCount(text) + " bytes)");
    }

    static void Discovery()
    {
        var sender = IPAddress.Parse("192.168.1.50");
        string notify = "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nServer: UPnP/1.0\r\nLocation: 192.168.1.50\r\nNT: urn:bambulab-com:device:3dprinter:1\r\n" +
                        "USN: 01P00A123456789\r\nCache-Control: max-age=1800\r\nDevModel.bambu.com: C12\r\nDevName.bambu.com: Workshop P1S\r\nDevConnect.bambu.com: cloud\r\nDevBind.bambu.com: occupied\r\n\r\n";
        var found = PrinterDiscovery.ParseSsdp(notify, sender);
        Check(found is { Kind: PrinterKind.Bambu, Address: "192.168.1.50", Serial: "01P00A123456789", Name: "Workshop P1S", Model: "P1S" },
              "discovery: a cloud-bound Bambu printer's SSDP announcement gives its address, serial, name and model");
        Check(PrinterDiscovery.ParseSsdp(notify.Replace("Location: 192.168.1.50", "Location: 192.168.1.99"), sender) is null, "discovery: an announcement for another address is ignored");
        Check(PrinterDiscovery.ParseSsdp(notify, IPAddress.Parse("8.8.8.8")) is null, "discovery: announcements from outside the LAN are ignored");
        Check(PrinterDiscovery.ParseSsdp(notify.Replace("bambulab-com", "example"), sender) is null && PrinterDiscovery.ParseSsdp("garbage", sender) is null, "discovery: other SSDP devices are ignored");
        // An mDNS answer from Moonraker: PTR to the instance, SRV with port 7125.
        var packet = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 2, 0, 0, 0, 0 };
        void Name(params string[] labels) { foreach (var l in labels) { packet.Add((byte)l.Length); packet.AddRange(Encoding.ASCII.GetBytes(l)); } packet.Add(0); }
        Name("_moonraker", "_tcp", "local"); packet.AddRange(new byte[] { 0, 12, 0, 1, 0, 0, 0, 120 });
        var instance = new List<byte>(); foreach (var l in new[] { "voron", "_moonraker", "_tcp", "local" }) { instance.Add((byte)l.Length); instance.AddRange(Encoding.ASCII.GetBytes(l)); } instance.Add(0);
        packet.Add(0); packet.Add((byte)instance.Count); packet.AddRange(instance);
        Name("voron", "_moonraker", "_tcp", "local"); packet.AddRange(new byte[] { 0, 33, 0x80, 1, 0, 0, 0, 120, 0, 7, 0, 0, 0, 0, 0x1B, 0xD5 });
        packet.AddRange(new byte[] { 0 }); // trailing empty target name
        var mdns = PrinterDiscovery.ParseMdns(packet.ToArray(), IPAddress.Parse("192.168.1.60"));
        Check(mdns.Count == 1 && mdns[0] is { Kind: PrinterKind.Moonraker, Address: "192.168.1.60:7125", Name: "voron" }, "discovery: an mDNS answer finds Moonraker and its port");
        Check(PrinterDiscovery.ParseMdns(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 9, 0, 0, 0, 0, 0xC0, 0xFF }, IPAddress.Parse("192.168.1.60")).Count == 0, "discovery: malformed mDNS is ignored");
        Check(PrinterSettings.ValidAddress("192.168.1.50", false) && PrinterSettings.ValidAddress("voron.local:7125", true) && !PrinterSettings.ValidAddress("192.168.1.50:80", false) &&
              !PrinterSettings.ValidAddress("http://x", true) && !PrinterSettings.ValidAddress("a/b", true) && !PrinterSettings.ValidAddress("user@host", true), "printers: addresses are a host and, for HTTP printers, a port");
        Check(new PrinterSettings(1, PrinterKind.Bambu, "P1S", "192.168.1.50", "01P00A123456789", "12345678").Problem() is null &&
              new PrinterSettings(1, PrinterKind.Bambu, "P1S", "192.168.1.50", "01P00A123456789", "1234567").Problem() == "printer_invalid_code" &&
              new PrinterSettings(1, PrinterKind.Bambu, "P1S", "192.168.1.50", "", "12345678").Problem() == "printer_invalid_serial" &&
              new PrinterSettings(1, PrinterKind.OctoPrint, "Octo", "octopi.local", "", "bad key").Problem() == "printer_invalid_key" &&
              new PrinterSettings(1, PrinterKind.Moonraker, "Voron", "voron.local:7125").Problem() is null, "printers: each kind's required fields are checked");
    }

    static void Guards()
    {
        string serial = "01P00A123456789", topic = BambuReadOnly.RequestTopic(serial);
        byte[] pushall = Encoding.UTF8.GetBytes(BambuReadOnly.PushAll);
        Check(MqttPackets.PublishPacket(topic, pushall)[0] == MqttPackets.Publish, "read-only: the full-status request can be sent");
        // Commands from Bambu's local protocol that change a printer. None may ever be built.
        string[] commands =
        [
            """{"print":{"sequence_id":"0","command":"pause"}}""",
            """{"print":{"sequence_id":"0","command":"resume"}}""",
            """{"print":{"sequence_id":"0","command":"stop"}}""",
            """{"print":{"sequence_id":"0","command":"gcode_line","param":"G28 \n"}}""",
            """{"print":{"sequence_id":"0","command":"project_file","url":"file:///sdcard/a.3mf","param":"Metadata/plate_1.gcode"}}""",
            """{"print":{"sequence_id":"0","command":"print_speed","param":"4"}}""",
            """{"system":{"sequence_id":"0","command":"ledctrl","led_node":"chamber_light","led_mode":"on"}}""",
            """{"print":{"sequence_id":"0","command":"ams_change_filament","target":255}}""",
            """{"upgrade":{"sequence_id":"0","command":"start"}}""",
            """{"pushing":{"sequence_id":"0","command":"pushall"},"print":{"command":"stop"}}""",
            """{"pushing":{"sequence_id":"0","command":"pushall"}} """,
            "",
        ];
        foreach (var command in commands)
            Throws<ReadOnlyViolationException>(() => MqttPackets.PublishPacket(topic, Encoding.UTF8.GetBytes(command)), "read-only: refuses to publish " + (command.Length > 40 ? command[..40] : command));
        foreach (var other in new[] { BambuReadOnly.ReportTopic(serial), "device/" + serial + "/request/x", "device/+/request", "device/../request", "#", "device//request" })
            Throws<ReadOnlyViolationException>(() => MqttPackets.PublishPacket(other, pushall), "read-only: refuses the full-status request on topic " + other);
        Check(typeof(MqttPackets).GetMethods().Count(m => m.ReturnType == typeof(byte[]) && m.GetParameters().Any(p => p.ParameterType == typeof(ReadOnlySpan<byte>))) == 1,
              "read-only: one packet builder takes a payload, and it is the guarded one");

        var guard = new HttpMessageInvoker(new ReadOnlyHttpGuard(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        foreach (var (method, path) in new[] { (HttpMethod.Post, "/printer/print/cancel"), (HttpMethod.Post, "/printer/gcode/script?script=G28"), (HttpMethod.Get, "/printer/gcode/script?script=G28"),
                     (HttpMethod.Post, "/api/job"), (HttpMethod.Post, "/api/files/local"), (HttpMethod.Put, "/api/v1/job/12/pause"), (HttpMethod.Delete, "/api/v1/job/12"), (HttpMethod.Get, "/api/files"),
                     (HttpMethod.Post, "/printer/emergency_stop"), (HttpMethod.Get, "/api/job?command=cancel") })
            Throws<ReadOnlyViolationException>(() => guard.SendAsync(new HttpRequestMessage(method, "http://printer" + path), CancellationToken.None).GetAwaiter().GetResult(),
                "read-only: refuses " + method + " " + path);
        foreach (var path in ReadOnlyHttpGuard.Paths)
            Check(guard.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://printer" + path), CancellationToken.None).GetAwaiter().GetResult().IsSuccessStatusCode, "read-only: allows GET " + path);
        Throws<ReadOnlyViolationException>(() => guard.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://printer/api/job") { Content = new StringContent("{\"command\":\"cancel\"}") }, CancellationToken.None).GetAwaiter().GetResult(),
            "read-only: a GET may not carry a body");

        var pin = new CertificatePin(null);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=01P00A123456789", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var first = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        using var second = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(2));
        Check(pin.Check(first) && pin.Fingerprint == CertificatePin.Of(first) && pin.Check(first) && !pin.Changed, "certificate: the first certificate is pinned and accepted again");
        Check(!pin.Check(second) && pin.Changed && !new CertificatePin(CertificatePin.Of(first)).Check(second), "certificate: a different certificate is refused");
    }

    // A fake printer on loopback, recording every packet ClawPal sends it.
    static async Task BrokerConversation()
    {
        foreach (bool refuse in new[] { false, true })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var sent = new List<(byte Header, byte[] Body)>();
            string serial = "01S00C123456789";
            var printer = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                async Task Next() { var packet = await MqttPackets.ReadAsync(stream, 1 << 20, CancellationToken.None); lock (sent) sent.Add(packet); }
                await Next();
                await stream.WriteAsync(new byte[] { MqttPackets.ConnAck, 2, 0, (byte)(refuse ? 5 : 0) });
                if (refuse) return;
                await Next(); await stream.WriteAsync(new byte[] { MqttPackets.SubAck, 3, 0, 1, 0 });
                await Next();
                foreach (var report in new[] { BambuFull, """{"print":{"mc_percent":43,"command":"push_status","msg":1}}""" })
                {
                    var body = new List<byte>(); var topic = Encoding.UTF8.GetBytes(BambuReadOnly.ReportTopic(serial));
                    body.Add((byte)(topic.Length >> 8)); body.Add((byte)topic.Length); body.AddRange(topic); body.AddRange(Encoding.UTF8.GetBytes(report));
                    var packet = new List<byte> { MqttPackets.Publish }; int length = body.Count;
                    do { byte digit = (byte)(length % 128); length /= 128; if (length > 0) digit |= 0x80; packet.Add(digit); } while (length > 0);
                    packet.AddRange(body);
                    await stream.WriteAsync(packet.ToArray());
                }
                await Task.Delay(300);
                try { while (client.Available > 0) await Next(); } catch (IOException) { }
            });
            var session = new BambuSession(serial, "12345678", async token =>
            {
                var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, token); return tcp.GetStream();
            });
            var links = new List<PrinterLink>();
            Exception? ended = null;
            try { await session.RunAsync(l => links.Add(l), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); }
            catch (Exception error) { ended = error; }
            await printer;
            listener.Stop();
            if (refuse) { Check(ended is UnauthorizedAccessException && !links.Contains(PrinterLink.Online), "mqtt: a refused access code is reported, not retried at once"); continue; }
            Check(ended is IOException && links.Contains(PrinterLink.Online), "mqtt: a closed connection ends the session for the worker to retry");
            Check(session.Status is { State: PrintStates.Printing, Progress: 43, Layer: 118, File: "Desk organizer" }, "mqtt: reports from the printer merge into its state");
            var connect = sent[0];
            Check((connect.Header & 0xF0) == MqttPackets.Connect && Encoding.UTF8.GetString(connect.Body).Contains("bblp") && Encoding.UTF8.GetString(connect.Body).Contains("12345678"),
                  "mqtt: connects as bblp with the access code");
            Check(sent.Count >= 3 && (sent[1].Header) == MqttPackets.Subscribe && Encoding.UTF8.GetString(sent[1].Body).Contains(BambuReadOnly.ReportTopic(serial)),
                  "mqtt: subscribes to the printer's report topic");
            var publishes = sent.Where(p => (p.Header & 0xF0) == MqttPackets.Publish).ToList();
            Check(publishes.Count == 1 && MqttPackets.ParsePublish(publishes[0].Header, publishes[0].Body) is var (t, payload) && t == BambuReadOnly.RequestTopic(serial) &&
                  Encoding.UTF8.GetString(payload.Span) == BambuReadOnly.PushAll, "read-only: the only message sent to the printer is one full-status request");
            Check(sent.All(p => p.Header == MqttPackets.Subscribe || (p.Header & 0xF0) is MqttPackets.Connect or MqttPackets.Publish or MqttPackets.PingReq or MqttPackets.Disconnect),
                  "read-only: nothing but connect, subscribe, that request, pings and disconnect reaches the printer");
        }
    }

    static async Task HttpPolling()
    {
        var seen = new List<HttpRequestMessage>();
        HttpResponseMessage Answer(HttpRequestMessage request)
        {
            lock (seen) seen.Add(request);
            string path = request.RequestUri!.PathAndQuery;
            string body = path switch
            {
                PrinterContract.MoonrakerQuery => """{"result":{"status":{"print_stats":{"state":"printing","filename":"a.gcode","print_duration":600},"display_status":{"progress":0.5},"extruder":{"temperature":200,"target":200}}}}""",
                "/api/job" => """{"job":{"file":{"name":"a.gcode"}},"progress":{"completion":50,"printTimeLeft":60},"state":"Printing"}""",
                "/api/printer" => """{"temperature":{"tool0":{"actual":200,"target":200}},"state":{"flags":{"operational":true,"printing":true}}}""",
                "/api/v1/status" => """{"job":{"id":1,"progress":50,"time_remaining":60},"printer":{"state":"PRINTING","temp_nozzle":200}}""",
                "/api/v1/job" => """{"id":1,"progress":50,"time_remaining":60,"file":{"display_name":"a.bgcode"}}""",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
        foreach (var kind in new[] { PrinterKind.Moonraker, PrinterKind.OctoPrint, PrinterKind.PrusaLink })
        {
            lock (seen) seen.Clear();
            var worker = new PrinterWorker(new PrinterSettings(1, kind, "Test", "printer.local:8080", "", "secret-key"), () => new StaticHandler(Answer))
                { ActivePoll = TimeSpan.FromMilliseconds(30), IdlePoll = TimeSpan.FromMilliseconds(30) };
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await worker.RunAsync(stop.Token);
            List<HttpRequestMessage> requests; lock (seen) requests = seen.ToList();
            Check(worker.Status is { State: PrintStates.Printing, Progress: 50 } && worker.Link == PrinterLink.Online, kind + ": a polled printer reports its progress");
            Check(requests.Count >= 2 && requests.All(r => r.Method == HttpMethod.Get && ReadOnlyHttpGuard.Paths.Contains(r.RequestUri!.PathAndQuery) && r.Content is null),
                  kind + ": every request is a GET to a status path (" + requests.Count + " requests)");
            Check(requests.All(r => r.Headers.TryGetValues("X-Api-Key", out var key) && key.Single() == "secret-key" && r.RequestUri!.Host == "printer.local" && r.RequestUri.Port == 8080),
                  kind + ": requests go to the printer's own address with its key");
        }
        // A printer that is off: the worker keeps trying, with growing waits, and reports it unreachable.
        int attempts = 0; var offLog = new List<string>();
        var off = new PrinterWorker(new PrinterSettings(2, PrinterKind.Moonraker, "Off", "printer.local"), () => new StaticHandler(_ => { Interlocked.Increment(ref attempts); throw new HttpRequestException("refused"); }))
            { Retry = TimeSpan.FromMilliseconds(20), MaxRetry = TimeSpan.FromMilliseconds(80), Log = line => { lock (offLog) offLog.Add(line); } };
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500))) await off.RunAsync(stop.Token);
        Check(off.Link == PrinterLink.Unreachable && off.Status.State == PrintStates.Offline && attempts is >= 3 and <= 25, "worker: a printer that is off keeps being retried with backoff (" + attempts + " tries)");
        Check(offLog.Count == 1 && offLog[0].Contains("unreachable"), "worker: a printer that stays off is logged once, not on every retry (" + offLog.Count + " lines)");
        var denied = new PrinterWorker(new PrinterSettings(3, PrinterKind.OctoPrint, "Octo", "octopi.local", "", "wrong"), () => new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200))) await denied.RunAsync(stop.Token);
        Check(denied.Link == PrinterLink.Denied, "worker: a refused API key is reported as such");
    }

    // Static checks over the source: printer code never sends anything but status requests.
    static void SourceScan()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "companion", "PrinterMqtt.cs"))) dir = dir.Parent;
        if (dir is null) throw new Exception("FAIL: companion sources not found");
        var sources = Directory.GetFiles(Path.Combine(dir.FullName, "companion"), "*.cs").Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f))).ToList();
        var publishes = sources.SelectMany(s => s.Text.Split('\n').Where(l => l.Contains("PublishPacket(") && !l.Contains("public static byte[] PublishPacket")).Select(l => (s.Name, Line: l.Trim()))).ToList();
        Check(publishes.Count == 1 && publishes[0].Name == "PrinterMqtt.cs" && publishes[0].Line.Contains("BambuReadOnly.RequestTopic(serial)") && publishes[0].Line.Contains("BambuReadOnly.PushAll"),
              "read-only: the companion publishes in one place, and only the full-status request");
        var printerFiles = sources.Where(s => s.Name.StartsWith("Printer") || s.Name == "SettingsForm.Printers.cs").ToList();
        Check(printerFiles.Count >= 5 && printerFiles.All(s => !s.Text.Contains("PostAsync") && !s.Text.Contains("PutAsync") && !s.Text.Contains("DeleteAsync") &&
              !s.Text.Contains("HttpMethod.Post") && !s.Text.Contains("HttpMethod.Put") && !s.Text.Contains("HttpMethod.Delete") && !s.Text.Contains("PatchAsync")),
              "read-only: printer code has no POST, PUT, PATCH or DELETE");
        Check(printerFiles.SelectMany(s => s.Text.Split('\n')).Where(l => l.Contains("Log")).All(l => !l.Contains("Secret") && !l.Contains("accessCode") && !l.Contains("secret.Text")),
              "privacy: access codes and keys are never written to the log");
    }

    sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(answer(request));
    }
}
