using System.Security.Cryptography;
using System.Text;

namespace ClawPal.Companion;

// Request authentication for one discovered display. Protocol 2 displays (discovery
// hello v2) get HMAC-signed requests bound to their challenge, so the pairing token
// never crosses the LAN and a captured request cannot be replayed. Older displays
// only understand the bearer token. firmware/main/network/buddy_auth.h is the other side.
public sealed class DeviceAuth(string token, string? challenge)
{
    public const string EmptyBodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    public const string BodyHashHeader = "X-ClawPal-Body-SHA256";
    private long counter;
    public bool Signed => challenge is not null;

    public static DeviceAuth For(DeviceEndpoint endpoint, string token) => new(token, endpoint.Challenge);

    // Each call uses the next counter, so build exactly one header per request sent.
    public string Authorization(string method, string pathAndQuery, string bodyHash)
    {
        if (challenge is null) return "Bearer " + token;
        long n = Interlocked.Increment(ref counter);
        return $"ClawPal-HMAC c={challenge},n={n},s={Signature(token, method, pathAndQuery, challenge, n, bodyHash)}";
    }

    public static string Signature(string token, string method, string pathAndQuery, string challenge, long counter, string bodyHash) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes($"clawpal-auth-v2\n{method}\n{pathAndQuery}\n{challenge}\n{counter}\n{bodyHash}")));

    public static string BodyHash(ReadOnlySpan<byte> body) => Hex(SHA256.HashData(body));
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    public HttpClient CreateClient(TimeSpan timeout, HttpMessageHandler? inner = null) =>
        new(new SigningHandler(this) { InnerHandler = inner ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false } }) { Timeout = timeout };

    // Signs every request at send time. Firmware uploads declare their image hash in
    // X-ClawPal-SHA256, which the display checks against the streamed bytes.
    private sealed class SigningHandler(DeviceAuth auth) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string bodyHash = EmptyBodyHash;
            if (request.Content is not null)
                bodyHash = request.Headers.TryGetValues("X-ClawPal-SHA256", out var declared) ? declared.Single().ToLowerInvariant()
                    : BodyHash(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            if (auth.Signed) { request.Headers.Remove(BodyHashHeader); request.Headers.Add(BodyHashHeader, bodyHash); }
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", auth.Authorization(request.Method.Method, request.RequestUri!.PathAndQuery, bodyHash));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
