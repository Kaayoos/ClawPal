using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClawPal.Companion;

internal sealed record ClaudeQuota(DateTimeOffset CapturedAt, CodexWindow Primary, CodexWindow Secondary);

internal sealed class ClaudeLiveLimits
{
    internal static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly HttpClient client;
    private readonly Func<ClaudeSession?, DateTimeOffset, ClaudeCredential?> credentials;
    private ClaudeCredential? credential;
    private ClaudeQuota? quota;
    private DateTimeOffset nextCredentialRead, nextAttempt;
    private string? identity, rejectedFingerprint;
    private string? selectedSession;
    private string status = "connecting";
    private int failures;

    internal ClaudeLiveLimits(HttpClient? client = null, Func<ClaudeSession?, DateTimeOffset, ClaudeCredential?>? credentials = null)
    {
        this.client = client ?? SharedClient;
        this.credentials = credentials ?? ClaudeCredentials.Read;
    }

    internal void Reset()
    {
        credential = null; quota = null; identity = rejectedFingerprint = selectedSession = null;
        nextCredentialRead = nextAttempt = default; failures = 0; status = "connecting";
    }

    internal async Task<CodexStats> RefreshAsync(CodexStats local, ClaudeSession? latest, DateTimeOffset now, CancellationToken cancellation)
    {
        if (now >= nextCredentialRead || selectedSession != latest?.Id)
        {
            selectedSession = latest?.Id;
            string? previousFingerprint = credential?.Fingerprint;
            credential = credentials(latest, now);
            nextCredentialRead = now.AddSeconds(10);
            if (identity != credential?.Identity)
            {
                quota = null; failures = 0; nextAttempt = default; rejectedFingerprint = null;
                identity = credential?.Identity;
            }
            else if (status == "signed_out" && previousFingerprint != credential?.Fingerprint)
                nextAttempt = default;
        }
        if (credential is null) return local.Available ? local : local with { Status = "signed_out" };
        if (credential.ExpiresAt <= now.AddSeconds(15).ToUnixTimeMilliseconds())
            status = "signed_out";
        else if (rejectedFingerprint == credential.Fingerprint) status = "signed_out";
        else if (now >= nextAttempt)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                request.Headers.UserAgent.ParseAdd("ClawPal/" + (typeof(ClaudeLiveLimits).Assembly.GetName().Version?.ToString(3) ?? "0.9.1"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var json = await ReadResponse(response.Content, timeout.Token);
                    quota = Parse(json.RootElement, now);
                    status = quota.Primary.Available || quota.Secondary.Available ? "live" : "no_limits";
                    failures = 0; rejectedFingerprint = null;
                    nextAttempt = now.AddSeconds(local.Running ? 60 : 300);
                }
                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Do not keep retrying the same refused token or refresh it ourselves.
                    rejectedFingerprint = credential.Fingerprint; status = "signed_out";
                    nextAttempt = now.AddMinutes(5);
                }
                else
                {
                    bool limited = response.StatusCode == HttpStatusCode.TooManyRequests;
                    status = limited ? "rate_limited" : "error";
                    nextAttempt = now + RetryDelay(response.Headers.RetryAfter, now, limited, ++failures);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                // Exception messages and response bodies can contain sensitive data.
                status = "error"; nextAttempt = now + RetryDelay(null, now, false, ++failures);
            }
        }
        return Merge(local, quota, now, status);
    }

    internal static TimeSpan RetryDelay(RetryConditionHeaderValue? retry, DateTimeOffset now, bool limited, int failures)
    {
        var explicitDelay = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.Zero);
        if (explicitDelay > TimeSpan.Zero) return explicitDelay;
        return TimeSpan.FromSeconds(limited ? 300 : Math.Min(300, 30 * Math.Pow(2, Math.Min(failures - 1, 4))));
    }

    private static async Task<JsonDocument> ReadResponse(HttpContent content, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > 65_536) throw new IOException("Claude usage response too large");
        using var stream = await content.ReadAsStreamAsync(cancellation);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (data.Length + count > 65_536) throw new IOException("Claude usage response too large");
            data.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    internal static ClaudeQuota Parse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || (!root.TryGetProperty("five_hour", out _) && !root.TryGetProperty("seven_day", out _)))
            throw new JsonException("Unrecognized Claude usage response");
        return new(now, Window(root, "five_hour", 300, now), Window(root, "seven_day", 10080, now));
    }

    private static CodexWindow Window(JsonElement root, string name, int minutes, DateTimeOffset now)
    {
        if (!CodexContract.Object(root, name, out var window) || !window.TryGetProperty("utilization", out var used) ||
            used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out double percentage) || !double.IsFinite(percentage) || percentage < 0) return CodexWindow.Empty;
        long resetAt = 0;
        string? reset = CodexContract.String(window, "resets_at");
        if (reset is not null)
        {
            if (!DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ||
                at <= now || at.ToUnixTimeSeconds() > uint.MaxValue) return CodexWindow.Empty;
            resetAt = at.ToUnixTimeSeconds();
        }
        return new(true, Math.Round(Math.Clamp(100 - percentage, 0, 100), 1), minutes, "", resetAt);
    }

    internal static CodexStats Merge(CodexStats local, ClaudeQuota? quota, DateTimeOffset now, string status)
    {
        if (quota is null) return local with { Available = false, Stale = false, Primary = CodexWindow.Empty, Secondary = CodexWindow.Empty, AgeS = 0, Status = status };
        long age = ActivityContract.Age(quota.CapturedAt, now);
        if (local.Available && !local.Stale && local.AgeS < age) return local;
        bool timely = quota.CapturedAt <= now.AddMinutes(1) && age <= 1800;
        CodexWindow Valid(CodexWindow window) => timely && (window.ResetsAt == 0 || window.ResetsAt > now.ToUnixTimeSeconds()) ? window : CodexWindow.Empty;
        var primary = Valid(quota.Primary); var secondary = Valid(quota.Secondary);
        bool available = primary.Available || secondary.Available;
        bool stale = available && (status != "live" || age > 120);
        return local with { Available = available, Primary = primary, Secondary = secondary, AgeS = available ? age : 0,
            Stale = stale, Status = available ? (stale ? "cached" : "live") : status == "live" ? "no_limits" : status };
    }
}
