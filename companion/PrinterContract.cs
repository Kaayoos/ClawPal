using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClawPal.Companion;

// The Printer app, seen from the PC. ClawPal reads 3D printers on the local network and tells the
// display how each print is going. Nothing here talks to a printer; the clients in PrinterClients.cs
// and PrinterMqtt.cs do, read-only. This file only turns what printers report into the telemetry shape.
public enum PrinterKind { Bambu, Moonraker, OctoPrint, PrusaLink }

public static class PrintStates
{
    // The names the display knows (buddy_telemetry_parse.c), in its order.
    public const string Offline = "offline", Idle = "idle", Preparing = "preparing", Printing = "printing",
        Paused = "paused", Finished = "finished", Failed = "failed";
    public const string NoReason = "", Filament = "filament", Error = "error", User = "user";
    public static readonly string[] All = [Offline, Idle, Preparing, Printing, Paused, Finished, Failed];
    public static bool Active(string state) => state is Preparing or Printing or Paused;
}

// What one printer reports right now. Temperatures are degrees Celsius, null when unknown.
public sealed record PrintStatus(string State, string Reason = "", int Progress = 0, long RemainingS = 0, int Layer = 0, int Layers = 0,
    string File = "", double? Nozzle = null, double? NozzleTarget = null, double? Bed = null, double? BedTarget = null, string Error = "")
{
    public static PrintStatus Offline { get; } = new(PrintStates.Offline);
}

// Telemetry: {"alerts":true,"printers":[{id,name,state,reason,progress,...}]}, at most four printers, busiest first.
public sealed record PrinterStats(bool Alerts, IReadOnlyList<PrinterItem> Printers);
public sealed record PrinterItem(int Id, string Name, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Progress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long RemainingS,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Layer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Layers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Nozzle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? NozzleTarget,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Bed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BedTarget,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);

public static class PrinterContract
{
    public const int MaxPrinters = 4, MaxNameBytes = 31, MaxFileBytes = 63, MaxErrorBytes = 31;
    public const long MaxRemainingS = 30L * 24 * 3600;

    static int Rank(string state) => state switch
    {
        PrintStates.Printing or PrintStates.Paused or PrintStates.Preparing => 0,
        PrintStates.Failed or PrintStates.Finished => 1,
        PrintStates.Idle => 2,
        _ => 3
    };

    static double? Temperature(double? value, double low, double high) =>
        value is { } v && double.IsFinite(v) && v >= low && v <= high ? Math.Round(v, 1) : null;

    // Bounds every value to what the display accepts, so one odd report cannot break a frame.
    public static PrinterItem Item(int id, string name, PrintStatus status)
    {
        string state = PrintStates.All.Contains(status.State) ? status.State : PrintStates.Idle;
        string? reason = status.Reason is PrintStates.Filament or PrintStates.Error or PrintStates.User ? status.Reason : null;
        string label = MusicContract.LimitText(name, MaxNameBytes);
        string file = MusicContract.LimitText(status.File, MaxFileBytes), error = MusicContract.LimitText(status.Error, MaxErrorBytes);
        return new(Math.Clamp(id, 1, 255), label.Length > 0 ? label : "Printer", state, reason,
            Math.Clamp(status.Progress, 0, 100), Math.Clamp(status.RemainingS, 0, MaxRemainingS),
            Math.Clamp(status.Layer, 0, 1_000_000), Math.Clamp(status.Layers, 0, 1_000_000), file.Length > 0 ? file : null,
            Temperature(status.Nozzle, -50, 600), Temperature(status.NozzleTarget, -50, 600),
            Temperature(status.Bed, -50, 300), Temperature(status.BedTarget, -50, 300), error.Length > 0 ? error : null);
    }

    public static PrinterStats Telemetry(bool alerts, IEnumerable<(int Id, string Name, PrintStatus Status)> printers) =>
        new(alerts, printers.Select(p => Item(p.Id, p.Name, p.Status)).OrderBy(p => Rank(p.State)).ThenBy(p => p.Id).Take(MaxPrinters).ToList());

    // ---- Bambu Lab: local MQTT reports ----

    // P1 and A1 printers send only the fields that changed; X1 and H2 send everything. Both are
    // merged into one state, so a field that has not changed keeps its last value.
    public static void Merge(JsonObject state, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is JsonObject child && state[key] is JsonObject existing) Merge(existing, child);
            else state[key] = value?.DeepClone();
        }
    }

    // Takes a report payload ({"print":{...}} and others) into the merged state. Returns false for
    // anything that is not a JSON object; reports without "print" change nothing.
    public static bool MergeReport(JsonObject state, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > 512 * 1024) return false;
        try
        {
            if (JsonNode.Parse(payload.ToArray()) is not JsonObject report) return false;
            if (report["print"] is JsonObject print)
            {
                if (state["print"] is not JsonObject merged) state["print"] = merged = new JsonObject();
                Merge(merged, print);
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    static long? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)Math.Round(d);
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }
    static double? Real(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)) return parsed;
        return null;
    }
    static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    // print_error as Bambu shows it, such as 0300_400C.
    public static string BambuErrorCode(long error) => $"{(error >> 16) & 0xFFFF:X4}_{error & 0xFFFF:X4}";
    // An HMS entry (attr, code) as Bambu's wiki writes it, such as 0700_2000_0002_0001.
    public static string HmsCode(long attr, long code) => $"{(attr >> 16) & 0xFFFF:X4}_{attr & 0xFFFF:X4}_{(code >> 16) & 0xFFFF:X4}_{code & 0xFFFF:X4}";
    // "The task was canceled" (0300_400C): someone stopped the print on purpose.
    public const long BambuCancelled = 0x0300400C;
    // Errors ending in 8011 are "filament ran out" for the AMS slots and the external spool.
    public static bool BambuRunout(long error) => (error & 0xFFFF) == 0x8011;

    public static PrintStatus FromBambu(JsonObject state)
    {
        if (state["print"] is not JsonObject print) return new(PrintStates.Idle);
        string gcode = Text(print["gcode_state"]).ToUpperInvariant();
        string status = gcode switch
        {
            "RUNNING" => PrintStates.Printing,
            "PREPARE" or "SLICING" or "INIT" => PrintStates.Preparing,
            "PAUSE" => PrintStates.Paused,
            "FINISH" => PrintStates.Finished,
            "FAILED" => PrintStates.Failed,
            _ => PrintStates.Idle
        };
        long error = Number(print["print_error"]) ?? 0;
        string code = error != 0 ? BambuErrorCode(error) : "";
        // The first HMS message worth a look (level 1-3 of 4), when print_error says nothing.
        bool serious = false;
        if (print["hms"] is JsonArray hms)
            foreach (var entry in hms)
            {
                long attr = Number(entry?["attr"]) ?? 0, value = Number(entry?["code"]) ?? 0;
                if (attr == 0 && value == 0) continue;
                long level = (value >> 16) & 0xFFFF;
                if (level is >= 1 and <= 3) { serious = true; if (code.Length == 0) code = HmsCode(attr, value); break; }
            }
        string reason = PrintStates.NoReason;
        if (status is PrintStates.Paused or PrintStates.Failed)
            reason = error == BambuCancelled ? PrintStates.User : BambuRunout(error) ? PrintStates.Filament :
                error != 0 || serious ? PrintStates.Error : status == PrintStates.Paused ? PrintStates.User : PrintStates.NoReason;
        if (reason == PrintStates.User && error == BambuCancelled) code = "";
        string file = Text(print["subtask_name"]);
        if (file.Length == 0) file = Path.GetFileNameWithoutExtension(Text(print["gcode_file"]).Replace('\\', '/').Split('/').Last());
        double? nozzle = Real(print["nozzle_temper"]), nozzleTarget = Real(print["nozzle_target_temper"]);
        // H2 printers with two nozzles pack each extruder's temperature as (target << 16) | current.
        if (nozzle is null && print["device"]?["extruder"]?["info"] is JsonArray extruders && extruders.Count > 0)
        {
            long active = Number(print["device"]?["extruder"]?["state"]) is { } bits ? (bits >> 4) & 0xF : 0;
            var info = extruders.FirstOrDefault(e => Number(e?["id"]) == active) ?? extruders[0];
            if (Number(info?["temp"]) is { } packed) { nozzle = packed & 0xFFFF; nozzleTarget = (packed >> 16) & 0xFFFF; }
        }
        bool working = PrintStates.Active(status) || status is PrintStates.Finished or PrintStates.Failed;
        return new(status, reason,
            status == PrintStates.Finished ? 100 : working ? (int)Math.Clamp(Number(print["mc_percent"]) ?? 0, 0, 100) : 0,
            PrintStates.Active(status) ? Math.Max(0, Number(print["mc_remaining_time"]) ?? 0) * 60 : 0,
            working ? (int)Math.Max(0, Number(print["layer_num"]) ?? 0) : 0, working ? (int)Math.Max(0, Number(print["total_layer_num"]) ?? 0) : 0,
            working ? file : "", nozzle, nozzleTarget, Real(print["bed_temper"]), Real(print["bed_target_temper"]),
            status is PrintStates.Paused or PrintStates.Failed ? code : "");
    }

    // ---- Klipper through Moonraker: GET /printer/objects/query ----
    public const string MoonrakerQuery = "/printer/objects/query?print_stats&display_status&virtual_sdcard&extruder&heater_bed";

    public static PrintStatus FromMoonraker(JsonElement root)
    {
        var status = root.GetProperty("result").GetProperty("status");
        JsonElement Get(JsonElement parent, string key) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? v : default;
        double? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
        var stats = Get(status, "print_stats");
        string klipper = Get(stats, "state").ValueKind == JsonValueKind.String ? Get(stats, "state").GetString() ?? "" : "";
        string state = klipper switch
        {
            "printing" => PrintStates.Printing, "paused" => PrintStates.Paused, "complete" => PrintStates.Finished,
            "cancelled" or "error" => PrintStates.Failed, _ => PrintStates.Idle
        };
        string reason = klipper == "cancelled" ? PrintStates.User : klipper == "error" ? PrintStates.Error : PrintStates.NoReason;
        double progress = Num(Get(Get(status, "display_status"), "progress")) ?? 0;
        if (progress <= 0) progress = Num(Get(Get(status, "virtual_sdcard"), "progress")) ?? 0;
        double printed = Num(Get(stats, "print_duration")) ?? 0;
        // Klipper does not predict the end; estimate it from the time spent printing so far.
        long remaining = state is PrintStates.Printing or PrintStates.Paused && progress > 0.01 && printed > 60 ?
            (long)Math.Round(printed / progress - printed) : 0;
        var info = Get(stats, "info");
        string file = Get(stats, "filename").ValueKind == JsonValueKind.String ? Get(stats, "filename").GetString() ?? "" : "";
        string message = klipper == "error" && Get(stats, "message").ValueKind == JsonValueKind.String ? Get(stats, "message").GetString() ?? "" : "";
        bool working = state != PrintStates.Idle;
        return new(state, reason, state == PrintStates.Finished ? 100 : working ? (int)Math.Clamp(Math.Floor(progress * 100), 0, 100) : 0, remaining,
            working ? (int)(Num(Get(info, "current_layer")) ?? 0) : 0, working ? (int)(Num(Get(info, "total_layer")) ?? 0) : 0,
            working ? Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/').Last()) : "",
            Num(Get(Get(status, "extruder"), "temperature")), Num(Get(Get(status, "extruder"), "target")),
            Num(Get(Get(status, "heater_bed"), "temperature")), Num(Get(Get(status, "heater_bed"), "target")), message);
    }

    // ---- OctoPrint: GET /api/job and GET /api/printer (null when no printer is connected to it) ----
    public static PrintStatus FromOctoPrint(JsonElement job, JsonElement? printer)
    {
        JsonElement Get(JsonElement parent, string key) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? v : default;
        double? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
        bool Flag(string name) => printer is { } p && Get(Get(Get(p, "state"), "flags"), name).ValueKind == JsonValueKind.True;
        if (printer is null) return PrintStatus.Offline;
        double completion = Num(Get(Get(job, "progress"), "completion")) ?? 0;
        string file = Get(Get(Get(job, "job"), "file"), "name") is { ValueKind: JsonValueKind.String } name ? name.GetString() ?? "" : "";
        string state = Flag("error") ? PrintStates.Failed : Flag("cancelling") ? PrintStates.Failed : Flag("paused") || Flag("pausing") ? PrintStates.Paused :
            Flag("printing") || Flag("finishing") ? PrintStates.Printing :
            completion >= 99.95 && file.Length > 0 ? PrintStates.Finished : PrintStates.Idle;
        string reason = Flag("error") ? PrintStates.Error : Flag("cancelling") ? PrintStates.User : PrintStates.NoReason;
        bool working = state != PrintStates.Idle;
        var temps = Get(printer.Value, "temperature");
        return new(state, reason, state == PrintStates.Finished ? 100 : working ? (int)Math.Clamp(Math.Floor(completion), 0, 100) : 0,
            PrintStates.Active(state) ? (long)(Num(Get(Get(job, "progress"), "printTimeLeft")) ?? 0) : 0, 0, 0,
            working ? Path.GetFileNameWithoutExtension(file) : "",
            Num(Get(Get(temps, "tool0"), "actual")), Num(Get(Get(temps, "tool0"), "target")),
            Num(Get(Get(temps, "bed"), "actual")), Num(Get(Get(temps, "bed"), "target")));
    }

    // ---- PrusaLink: GET /api/v1/status and GET /api/v1/job (null when there is no job) ----
    public static PrintStatus FromPrusaLink(JsonElement status, JsonElement? job)
    {
        JsonElement Get(JsonElement parent, string key) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? v : default;
        double? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
        var printer = Get(status, "printer");
        string prusa = Get(printer, "state").ValueKind == JsonValueKind.String ? Get(printer, "state").GetString()!.ToUpperInvariant() : "";
        string state = prusa switch
        {
            "PRINTING" => PrintStates.Printing, "PAUSED" or "ATTENTION" => PrintStates.Paused, "FINISHED" => PrintStates.Finished,
            "STOPPED" or "ERROR" => PrintStates.Failed, "BUSY" when job is not null => PrintStates.Preparing, _ => PrintStates.Idle
        };
        string reason = prusa == "STOPPED" ? PrintStates.User : prusa == "ERROR" ? PrintStates.Error : PrintStates.NoReason;
        var source = job ?? Get(status, "job");
        bool working = state != PrintStates.Idle;
        var file = Get(source, "file");
        string name = Get(file, "display_name").ValueKind == JsonValueKind.String ? Get(file, "display_name").GetString() ?? "" :
            Get(file, "name").ValueKind == JsonValueKind.String ? Get(file, "name").GetString() ?? "" : "";
        double progress = Num(Get(source, "progress")) ?? Num(Get(Get(status, "job"), "progress")) ?? 0;
        return new(state, reason, state == PrintStates.Finished ? 100 : working ? (int)Math.Clamp(Math.Floor(progress), 0, 100) : 0,
            PrintStates.Active(state) ? (long)(Num(Get(source, "time_remaining")) ?? Num(Get(Get(status, "job"), "time_remaining")) ?? 0) : 0, 0, 0,
            working ? Path.GetFileNameWithoutExtension(name) : "",
            Num(Get(printer, "temp_nozzle")), Num(Get(printer, "target_nozzle")), Num(Get(printer, "temp_bed")), Num(Get(printer, "target_bed")));
    }
}
