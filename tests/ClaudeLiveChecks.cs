using ClawPal.Companion;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ClaudeLiveChecks
{
    internal static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS: Claude live " + message); }
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        string Payload(double five = 64, double week = 31) => JsonSerializer.Serialize(new {
            five_hour = new { utilization = five, resets_at = now.AddHours(3).ToString("O") },
            seven_day = new { utilization = week, resets_at = now.AddDays(2).ToString("O") }, ignored_private_field = "secret"
        });
        HttpResponseMessage Ok(string? text = null) => new(HttpStatusCode.OK) { Content = new StringContent(text ?? Payload()) };
        var local = CodexStats.Empty(true, "no_limits") with { Activity = new(true, false, "local", 0, 1, 1, []) };
        var token = new ClaudeCredential("account-a", "fixture-access-token", now.AddDays(1).ToUnixTimeMilliseconds());
        var handler = new FakeHandler(); using var client = new HttpClient(handler);
        var monitor = new ClaudeLiveLimits(client, (_, _) => token);
        handler.Replies.Enqueue(() => Ok());
        var value = await monitor.RefreshAsync(local, null, now, default);
        Check(value.Available && !value.Stale && value.Primary.RemainingPercent == 36 && value.Secondary.RemainingPercent == 69, "fresh endpoint percentages are correctly converted to remaining usage");
        Check(value.Primary.ResetsAt == now.AddHours(3).ToUnixTimeSeconds() && value.Secondary.WindowMinutes == 10080, "offset-aware reset times and independent windows are retained");
        Check(ReferenceEquals(value.Activity, local.Activity) && value.Running, "live quota preserves task activity");
        Check(handler.Requests == 1 && handler.CorrectEndpoint && handler.CorrectHeaders, "only the fixed Anthropic endpoint receives the bearer and beta header");
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(59), default);
        Check(handler.Requests == 1 && value.AgeS == 59, "no polling inside the one-minute interval and age is not rewritten");
        handler.Replies.Enqueue(() => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)) } });
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(60), default);
        Check(value.Available && value.Stale && value.AgeS == 60, "a failed refresh keeps honest cached data instead of blinking unavailable");
        token = new("account-a", "rotated-token", now.AddDays(1).ToUnixTimeMilliseconds());
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(659), default);
        Check(handler.Requests == 2 && value.AgeS == 659, "Retry-After survives token rotation and normal ticks");
        handler.Replies.Enqueue(() => Ok(Payload(70, 32)));
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(660), default);
        Check(handler.Requests == 3 && !value.Stale && value.AgeS == 0 && value.Primary.RemainingPercent == 30, "refresh resumes when Retry-After expires");
        token = new("account-b", "another-account-token", now.AddDays(1).ToUnixTimeMilliseconds());
        handler.Replies.Enqueue(() => new(HttpStatusCode.ServiceUnavailable));
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(670), default);
        Check(!value.Available, "switching accounts cannot display previous account limits on a failed request");
        handler.Replies.Enqueue(() => new(HttpStatusCode.Unauthorized));
        await monitor.RefreshAsync(local, null, now.AddSeconds(700), default);
        int afterRejection = handler.Requests;
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(1300), default);
        Check(handler.Requests == afterRejection && value.Status == "signed_out", "a refused access token is not repeatedly retried or renewed by ClawPal");
        token = new("account-b", "renewed-by-claude", now.AddDays(1).ToUnixTimeMilliseconds());
        handler.Replies.Enqueue(() => Ok());
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(1310), default);
        Check(value.Available && !value.Stale && handler.Requests == afterRejection + 1, "Claude renewing its own token restores polling");
        token = new("account-c", "expired", now.AddMinutes(-1).ToUnixTimeMilliseconds());
        value = await monitor.RefreshAsync(local, null, now.AddSeconds(1320), default);
        Check(!value.Available && handler.Requests == afterRejection + 1, "expired credentials cause no network request");
        monitor.Reset(); token = new("account-d", "valid", now.AddDays(1).ToUnixTimeMilliseconds());
        handler.Replies.Enqueue(() => Ok(new string(' ', 65_537)));
        value = await monitor.RefreshAsync(local, null, now, default);
        Check(!value.Available && value.Status == "error", "oversized responses fail safely");
        monitor.Reset(); handler.Replies.Enqueue(() => Ok("{broken"));
        value = await monitor.RefreshAsync(local, null, now, default);
        Check(!value.Available, "malformed JSON cannot produce invented limits");
        monitor.Reset(); handler.Replies.Enqueue(() => Ok("{\"five_hour\":null,\"seven_day\":null}"));
        value = await monitor.RefreshAsync(local, null, now, default);
        Check(!value.Available && value.Status == "no_limits", "explicitly absent subscription windows are unavailable rather than full");
        using var payload = JsonDocument.Parse(Payload(100, 120));
        var quota = ClaudeLiveLimits.Parse(payload.RootElement, now);
        Check(quota.Primary.Available && quota.Primary.RemainingPercent == 0 && quota.Secondary.RemainingPercent == 0, "exhausted and overage percentages never become negative remaining usage");
        Check(!ClaudeLiveLimits.Merge(local, quota, now.AddMinutes(31), "error").Available, "cached endpoint readings expire after thirty minutes");
        Check(ClaudeLiveLimits.RetryDelay(new RetryConditionHeaderValue(now.AddMinutes(4)), now, true, 1) == TimeSpan.FromMinutes(4), "HTTP-date Retry-After is respected");
        Check(ClaudeLiveLimits.RetryDelay(null, now, true, 1) >= TimeSpan.FromMinutes(5), "429 without a retry header uses a conservative delay");
        Check(!new Configuration().ClaudeLiveUsage, "new and existing installations keep live access off until opted in");
        Check(!ClaudeLiveLimits.Merge(local with {Available=true,Primary=new(true,99,300,"",0)},null,now,"error").Available, "an account switch cannot revive an unscoped local reading when the new request fails");

        const string account = "10000000-0000-0000-0000-000000000001", org = "20000000-0000-0000-0000-000000000002", other = "30000000-0000-0000-0000-000000000003";
        string Key(string a = account, string o = org, string host = "https://api.anthropic.com") => $"acct:{a}|{other}:{o}:{host}:user:inference user:profile user:sessions:claude_code";
        var entry = new { token = "fixture-secret", expiresAt = now.AddHours(1).ToUnixTimeMilliseconds(), refreshToken = "must-not-use" };
        ClaudeCredential? Read(Dictionary<string, object> entries, string? organization = org)
        { using var json = JsonDocument.Parse(JsonSerializer.Serialize(entries)); return ClaudeCredentials.ParseDesktop(json.RootElement, "fixture-root", account, organization, now); }
        var parsed = Read(new() { [Key()] = entry });
        Check(parsed?.Token == "fixture-secret", "scoped Desktop V2 cache selects the active account and organization");
        Check(!JsonSerializer.Serialize(parsed).Contains("fixture-secret") && !parsed!.ToString().Contains("fixture-secret"), "credentials cannot appear in ordinary serialization or ToString diagnostics");
        Check(Read(new() { [Key(a: other)] = entry }) is null, "another signed-in account is excluded");
        Check(Read(new() { [Key(o: other)] = entry }) is null, "another organization is excluded");
        Check(Read(new() { [Key(host: "https://untrusted.invalid")] = entry }) is null, "custom API hosts cannot redirect the usage token");
        Check(Read(new() { [Key()] = entry, [Key(o: other)] = entry }, null) is null, "ambiguous organizations fail closed without a matched Desktop session");
        Check(Read(new() { [Key()] = entry }, null) is not null, "a single current Desktop organization works without session hooks");
        using var cli = JsonDocument.Parse(JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "cli-fixture", expiresAt = now.AddDays(1).ToUnixTimeMilliseconds() } }));
        Check(ClaudeCredentials.ParseCli(cli.RootElement, "custom-config")?.Token == "cli-fixture", "native CLI credentials have an independent reader");
        byte[] key = RandomNumberGenerator.GetBytes(32), plain = Encoding.UTF8.GetBytes("fixture-only"), encrypted = new byte[plain.Length + 31];
        "v10"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(3, 12));
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(encrypted.AsSpan(3, 12), plain, encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(encrypted.Length - 16));
        Check(ClaudeCredentials.DecryptChromium(encrypted, key).SequenceEqual(plain), "Desktop Chromium AES-GCM format is authenticated and decoded");
        encrypted[^1] ^= 1; bool rejected = false;
        try { ClaudeCredentials.DecryptChromium(encrypted, key); } catch (CryptographicException) { rejected = true; }
        Check(rejected, "tampered encrypted token caches are rejected");
        return passed;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        internal Queue<Func<HttpResponseMessage>> Replies { get; } = new();
        internal int Requests;
        internal bool CorrectEndpoint = true, CorrectHeaders = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            CorrectEndpoint &= request.Method == HttpMethod.Get && request.RequestUri == ClaudeLiveLimits.Endpoint;
            CorrectHeaders &= request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20";
            if (Replies.Count == 0) throw new Exception("Unexpected extra Claude request");
            return Task.FromResult(Replies.Dequeue()());
        }
    }
}
