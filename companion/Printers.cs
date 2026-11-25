using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawPal.Companion;

// A printer as saved in %USERPROFILE%\.clawpal\printers.json. The access code or API key is kept
// like the pairing token: encrypted with Windows DPAPI for this Windows account. It never goes to the
// display, into telemetry or into the log.
public sealed class PrinterEntry
{
    public int Id { get; set; }
    public PrinterKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Serial { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    // SHA-256 of the Bambu printer's certificate, learned on the first connection.
    public string? Pin { get; set; }
}

public sealed class PrinterBook
{
    public static string PathName => Path.Combine(Configuration.DirectoryPath, "printers.json");
    public bool Alerts { get; set; } = true;
    public List<PrinterEntry> Printers { get; set; } = [];
    [JsonIgnore] public int Revision { get; private set; }
    readonly object gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static PrinterBook Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            var book = JsonSerializer.Deserialize<PrinterBook>(File.ReadAllText(PathName), Json) ?? new();
            book.Printers = book.Printers.Where(p => p.Id is >= 1 and <= 255).GroupBy(p => p.Id).Select(g => g.First()).Take(PrinterContract.MaxPrinters).ToList();
            return book;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Write("Printers file was unreadable and was ignored: " + error.GetType().Name);
            return new();
        }
    }

    void Save()
    {
        Directory.CreateDirectory(Configuration.DirectoryPath);
        string temporary = PathName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, PathName, true);
        ++Revision;
    }

    // Each printer with its secret decrypted, for the workers. A secret this account cannot decrypt
    // (a copied file) leaves the printer without one, so it shows as refused rather than vanishing.
    public List<PrinterSettings> Settings()
    {
        lock (gate)
            return Printers.Select(p =>
            {
                string secret = "";
                if (p.ProtectedSecret.Length > 0)
                    try { secret = SecretProtection.Unprotect(p.ProtectedSecret); }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or FormatException) { }
                return new PrinterSettings(p.Id, p.Kind, p.Name, p.Address, p.Serial, secret, p.Pin);
            }).ToList();
    }

    public List<PrinterEntry> Entries() { lock (gate) return Printers.Select(p => new PrinterEntry { Id = p.Id, Kind = p.Kind, Name = p.Name, Address = p.Address, Serial = p.Serial }).ToList(); }
    public bool AlertsOn { get { lock (gate) return Alerts; } }

    // Validates, then saves. Throws ArgumentException with a message for the user.
    public PrinterSettings Add(PrinterKind kind, string name, string address, string serial, string secret)
    {
        lock (gate)
        {
            if (Printers.Count >= PrinterContract.MaxPrinters) throw new ArgumentException(L.Text("printer_limit"));
            int id = Enumerable.Range(1, 255).First(n => Printers.All(p => p.Id != n));
            var printer = new PrinterSettings(id, kind, name.Trim(), address.Trim(), serial.Trim().ToUpperInvariant(), secret.Trim());
            if (printer.Problem() is { } problem) throw new ArgumentException(L.Text(problem));
            if (Printers.Any(p => p.Kind == kind && (string.Equals(p.Address, printer.Address, StringComparison.OrdinalIgnoreCase) || (kind == PrinterKind.Bambu && p.Serial == printer.Serial))))
                throw new ArgumentException(L.Text("printer_duplicate"));
            Printers.Add(new PrinterEntry { Id = id, Kind = kind, Name = printer.Name, Address = printer.Address, Serial = printer.Serial,
                ProtectedSecret = printer.Secret.Length > 0 ? SecretProtection.Protect(printer.Secret) : "" });
            Save();
            Log.Write($"Printer {id} added ({kind}).");
            return printer;
        }
    }

    public void Remove(int id) { lock (gate) { if (Printers.RemoveAll(p => p.Id == id) > 0) { Save(); Log.Write($"Printer {id} removed."); } } }
    public void SetAlerts(bool value) { lock (gate) { if (Alerts == value) return; Alerts = value; Save(); } }
    public void SetPin(int id, string pin) { lock (gate) { if (Printers.FirstOrDefault(p => p.Id == id) is { Pin: null } printer) { printer.Pin = pin; Save(); Log.Write($"Printer {id}: certificate pinned."); } } }
}

// Keeps one worker per saved printer while the Printer app is on, and hands the display a snapshot.
public sealed class PrinterMonitor
{
    public PrinterBook Book { get; } = PrinterBook.Load();
    public Func<bool> Enabled { get; init; } = () => true;
    readonly Dictionary<int, (PrinterWorker Worker, CancellationTokenSource Stop, Task Run)> workers = new();
    readonly object gate = new();

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Reconcile(token);
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            List<Task> running;
            lock (gate) { foreach (var w in workers.Values) w.Stop.Cancel(); running = workers.Values.Select(w => w.Run).ToList(); workers.Clear(); }
            try { await Task.WhenAll(running); } catch (OperationCanceledException) { }
        }
    }

    void Reconcile(CancellationToken token)
    {
        var wanted = Enabled() ? Book.Settings() : [];
        lock (gate)
        {
            foreach (var id in workers.Keys.ToList())
            {
                var current = workers[id].Worker.Settings;
                if (wanted.FirstOrDefault(s => s.Id == id) is { } next && next with { Pin = null } == current with { Pin = null }) continue;
                workers[id].Stop.Cancel();
                workers.Remove(id);
            }
            foreach (var settings in wanted.Where(s => !workers.ContainsKey(s.Id)))
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var worker = new PrinterWorker(settings) { Log = Log.Write };
                worker.Pinned += pin => Book.SetPin(settings.Id, pin);
                var run = Task.Run(() => worker.RunAsync(stop.Token), stop.Token);
                _ = run.ContinueWith(t => Log.Write($"Printer {settings.Id} worker stopped: {t.Exception?.GetBaseException().GetType().Name}"), TaskContinuationOptions.OnlyOnFaulted);
                workers[settings.Id] = (worker, stop, run);
            }
        }
    }

    // Telemetry for the display, or null while the Printer app is off.
    public PrinterStats? Snapshot()
    {
        if (!Enabled()) return null;
        var entries = Book.Entries();
        lock (gate)
            return PrinterContract.Telemetry(Book.AlertsOn, entries.Select(e =>
                (e.Id, e.Name, workers.TryGetValue(e.Id, out var w) ? w.Worker.Status : PrintStatus.Offline)));
    }

    // The status line under a printer in the PC app.
    public string StatusText(int id)
    {
        PrinterWorker? worker;
        lock (gate) worker = workers.TryGetValue(id, out var w) ? w.Worker : null;
        if (worker is null) return Enabled() ? L.Text("printer_status_connecting") : L.Text("printers_off_in_apps");
        var status = worker.Status;
        return worker.Link switch
        {
            PrinterLink.Denied => L.Text("printer_status_denied"),
            PrinterLink.Certificate => L.Text("printer_status_certificate"),
            PrinterLink.Busy => L.Text("printer_status_busy"),
            PrinterLink.Unreachable => L.Text("printer_status_unreachable"),
            PrinterLink.Connecting => L.Text("printer_status_connecting"),
            _ => status.State switch
            {
                PrintStates.Printing => L.Text("printer_status_printing_0", status.Progress),
                PrintStates.Preparing => L.Text("printer_status_0", L.Text("printer_state_preparing")),
                PrintStates.Paused => L.Text("printer_status_0", L.Text("printer_state_paused")),
                PrintStates.Finished => L.Text("printer_status_0", L.Text("printer_state_finished")),
                PrintStates.Failed => L.Text("printer_status_0", L.Text("printer_state_failed")),
                _ => L.Text("printer_status_ready")
            }
        };
    }
}
