using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace ClawPal.Companion;

// One printer as the PC app keeps it. Secret is the Bambu access code or an API key, held in memory
// only; Printers.cs stores it with Windows DPAPI. Pin is the Bambu certificate's SHA-256.
public sealed record PrinterSettings(int Id, PrinterKind Kind, string Name, string Address, string Serial = "", string Secret = "", string? Pin = null)
{
    // A host name or IPv4 address, with a port for the HTTP printers. No scheme, path or user info.
    public static bool ValidAddress(string? address, bool allowPort)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 253 || address.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '?' or '#')) return false;
        string host = address;
        int colon = address.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!allowPort || !int.TryParse(address[(colon + 1)..], out int port) || port is < 1 or > 65535) return false;
            host = address[..colon];
        }
        return Uri.CheckHostName(host) is UriHostNameType.IPv4 or UriHostNameType.Dns;
    }
    public static bool ValidKey(string? key) => key is not null && key.Length <= 128 && key.All(c => c is > ' ' and < (char)127);
    // The first problem with this printer, as a catalog key, or null when it can be saved.
    public string? Problem()
    {
        if (Id is < 1 or > 255 || string.IsNullOrWhiteSpace(Name) || System.Text.Encoding.UTF8.GetByteCount(Name) > PrinterContract.MaxNameBytes) return "printer_invalid_name";
        if (!ValidAddress(Address, Kind != PrinterKind.Bambu)) return "printer_invalid_address";
        if (Kind == PrinterKind.Bambu && !BambuReadOnly.ValidSerial(Serial)) return "printer_invalid_serial";
        if (Kind == PrinterKind.Bambu && !BambuReadOnly.ValidAccessCode(Secret)) return "printer_invalid_code";
        if (Kind != PrinterKind.Bambu && !ValidKey(Secret)) return "printer_invalid_key";
        return null;
    }
}

// Every request a printer client makes passes through here: GET, and only to the status paths below.
// Anything else (a POST to start or cancel a job, a file upload, a G-code command) throws before it
// leaves the PC.
public sealed class ReadOnlyHttpGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public static readonly string[] Paths = [PrinterContract.MoonrakerQuery, "/api/job", "/api/printer", "/api/v1/status", "/api/v1/job"];
    public static bool Allowed(HttpMethod method, Uri? uri) => method == HttpMethod.Get && uri is not null && Paths.Contains(uri.PathAndQuery, StringComparer.Ordinal);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (!Allowed(request.Method, request.RequestUri) || request.Content is not null)
            throw new ReadOnlyViolationException("ClawPal only reads printer status; refused " + request.Method + " " + request.RequestUri?.PathAndQuery);
        return base.SendAsync(request, token);
    }
}

// Polls or listens to one printer for as long as it runs, reconnecting when it is off or away.
// One connection per printer: printers allow only a few clients at once.
public sealed class PrinterWorker
{
    readonly PrinterSettings settings;
    readonly Func<HttpMessageHandler> handler;
    readonly Func<CertificatePin, Func<CancellationToken, Task<Stream>>>? bambuOpen;
    readonly object gate = new();
    PrintStatus status = PrintStatus.Offline;
    PrinterLink link = PrinterLink.Connecting;
    public CertificatePin Pin { get; }
    public PrinterSettings Settings => settings;
    public PrintStatus Status { get { lock (gate) return status; } }
    public PrinterLink Link { get { lock (gate) return link; } }
    // Raised when the printer reports a certificate for the first time, so it can be saved.
    public event Action<string>? Pinned;
    public Action<string>? Log { get; init; }
    // Short waits in tests; the real ones are below.
    public TimeSpan ActivePoll { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Refused { get; init; } = TimeSpan.FromMinutes(5);

    public PrinterWorker(PrinterSettings settings, Func<HttpMessageHandler>? handler = null, Func<CertificatePin, Func<CancellationToken, Task<Stream>>>? bambuOpen = null)
    {
        this.settings = settings;
        this.handler = handler ?? (() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 1, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8), UseCookies = false,
            // PrusaLink on newer firmware asks for HTTP digest with the user "maker".
            Credentials = settings.Kind == PrinterKind.PrusaLink && settings.Secret.Length > 0 ? new NetworkCredential("maker", settings.Secret) : null,
        });
        this.bambuOpen = bambuOpen;
        Pin = new CertificatePin(settings.Pin);
    }

    // The last problem written to the log. Printers are often switched off, so each problem is logged once,
    // and once more when the printer answers again, rather than on every retry.
    PrinterLink? logged;
    void Set(PrintStatus? next, PrinterLink state)
    {
        lock (gate) { if (next is not null) status = next; link = state; }
        if (state == PrinterLink.Online && logged is not null) { logged = null; Log?.Invoke($"Printer {settings.Id} reachable again"); }
    }
    // Logs a problem only when it differs from the one already logged.
    void Report(PrinterLink state, string message)
    {
        if (logged == state) return;
        logged = state; Log?.Invoke(message);
    }

    public async Task RunAsync(CancellationToken token)
    {
        var wait = Retry;
        while (!token.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                if (settings.Kind == PrinterKind.Bambu) await RunBambuAsync(token);
                else await PollAsync(() => wait = Retry, token);
                delay = Retry;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (UnauthorizedAccessException) { Set(PrintStatus.Offline, PrinterLink.Denied); delay = Refused; Report(PrinterLink.Denied, $"Printer {settings.Id}: credentials refused"); }
            catch (AuthenticationException) when (Pin.Changed) { Set(PrintStatus.Offline, PrinterLink.Certificate); delay = Refused; Report(PrinterLink.Certificate, $"Printer {settings.Id}: certificate changed; not connecting"); }
            catch (PrinterBusyException) { Set(PrintStatus.Offline, PrinterLink.Busy); delay = TimeSpan.FromSeconds(30); }
            catch (Exception error) when (error is IOException or SocketException or HttpRequestException or AuthenticationException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or ObjectDisposedException)
            {
                if (error is ReadOnlyViolationException) throw;
                Set(PrintStatus.Offline, PrinterLink.Unreachable);
                delay = wait; wait = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, wait.Ticks * 2));
                Report(PrinterLink.Unreachable, $"Printer {settings.Id} unreachable: {error.GetType().Name}; logged again only when it answers");
            }
            try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
        }
    }

    async Task RunBambuAsync(CancellationToken token)
    {
        var open = (bambuOpen ?? (pin => BambuSession.Tls(settings.Address, pin)))(Pin);
        string? before = Pin.Fingerprint;
        var session = new BambuSession(settings.Serial, settings.Secret, open);
        session.Updated += () => Set(session.Status, PrinterLink.Online);
        await session.RunAsync(state =>
        {
            Set(state == PrinterLink.Online ? session.Status : null, state);
            if (state == PrinterLink.Online && before is null && Pin.Fingerprint is { } learned) Pinned?.Invoke(learned);
        }, token);
        throw new IOException("The printer closed the connection");
    }

    async Task<JsonDocument?> GetAsync(HttpClient http, string path, CancellationToken token, bool absentIsNull = false)
    {
        using var response = await http.GetAsync(path, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        // OctoPrint answers 409 while no printer is connected to it; PrusaLink 204 when there is no job.
        if (absentIsNull && response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
    }

    async Task PollAsync(Action reachable, CancellationToken token)
    {
        using var http = new HttpClient(new ReadOnlyHttpGuard(handler()), true)
        {
            BaseAddress = new Uri("http://" + settings.Address), Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 256 * 1024
        };
        if (settings.Secret.Length > 0) http.DefaultRequestHeaders.Add("X-Api-Key", settings.Secret);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        while (!token.IsCancellationRequested)
        {
            PrintStatus next;
            switch (settings.Kind)
            {
                case PrinterKind.Moonraker:
                    using (var query = await GetAsync(http, PrinterContract.MoonrakerQuery, token)) next = PrinterContract.FromMoonraker(query!.RootElement);
                    break;
                case PrinterKind.OctoPrint:
                    using (var job = await GetAsync(http, "/api/job", token))
                    using (var printer = await GetAsync(http, "/api/printer", token, true))
                        next = PrinterContract.FromOctoPrint(job!.RootElement, printer?.RootElement);
                    break;
                default:
                    using (var state = await GetAsync(http, "/api/v1/status", token))
                    {
                        bool hasJob = state!.RootElement.TryGetProperty("job", out var summary) && summary.ValueKind == JsonValueKind.Object;
                        using var job = hasJob ? await GetAsync(http, "/api/v1/job", token, true) : null;
                        next = PrinterContract.FromPrusaLink(state.RootElement, job?.RootElement);
                    }
                    break;
            }
            Set(next, PrinterLink.Online);
            reachable();
            await Task.Delay(PrintStates.Active(next.State) ? ActivePoll : IdlePoll, token);
        }
    }
}
