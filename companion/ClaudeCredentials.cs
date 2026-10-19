using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawPal.Companion;

// Never serialize this type or interpolate it in diagnostics. Claude owns token
// renewal; ClawPal only reads its existing access token after an explicit opt-in.
internal sealed class ClaudeCredential(string identity, string token, long expiresAt)
{
    [JsonIgnore] internal string Identity { get; } = identity;
    [JsonIgnore] internal string Token { get; } = token;
    [JsonIgnore] internal long ExpiresAt { get; } = expiresAt;
    [JsonIgnore] internal string Fingerprint { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public override string ToString() => "Claude credential (redacted)";
}

internal static class ClaudeCredentials
{
    internal static ClaudeCredential? Read(ClaudeSession? latest, DateTimeOffset now)
    {
        foreach (string root in ClaudeDesktop.Roots())
        {
            bool matched = false;
            try
            {
                using var config = ClaudeDesktop.ReadJson(Path.Combine(root, "config.json"));
                if (config is null) continue;
                string? organization = latest is null ? null : ClaudeDesktop.Organization(root, config.RootElement, latest.Id);
                if (latest is not null && organization is null) continue;
                matched = true;
                // A matched Desktop session must never inherit CLI credentials.
                return ReadDesktop(root, config.RootElement, organization, now);
            }
            catch (Exception e) when (SafeFailure(e)) { if (matched) return null; }
        }
        try
        {
            using var json = ClaudeDesktop.ReadJson(Path.Combine(ClaudeBridge.ClaudeDirectory, ".credentials.json"), 65_536);
            if (json is null) return null;
            return ParseCli(json.RootElement, ClaudeBridge.ClaudeDirectory);
        }
        catch (Exception e) when (SafeFailure(e)) { return null; }
    }

    private static ClaudeCredential? ReadDesktop(string root, JsonElement config, string? organization, DateTimeOffset now)
    {
        if (!Guid.TryParse(CodexContract.String(config, "lastKnownAccountUuid"), out var account)) return null;
        // V2 is authoritative when present, including an empty or invalid cache.
        string? encoded = CodexContract.String(config, "oauth:tokenCacheV2") ?? CodexContract.String(config, "oauth:tokenCache");
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 262_144) return null;
        byte[] encrypted = Convert.FromBase64String(encoded), plain;
        if (encrypted.AsSpan().StartsWith("v10"u8))
        {
            using var state = ClaudeDesktop.ReadJson(Path.Combine(root, "Local State"), 65_536);
            if (state is null || !CodexContract.Object(state.RootElement, "os_crypt", out var crypt)) return null;
            string? savedKey = CodexContract.String(crypt, "encrypted_key");
            if (savedKey is null || savedKey.Length > 16_384) return null;
            byte[] wrapped = Convert.FromBase64String(savedKey);
            if (!wrapped.AsSpan().StartsWith("DPAPI"u8)) return null;
            byte[] key = SecretProtection.UnprotectBytes(wrapped[5..]);
            try { plain = DecryptChromium(encrypted, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else plain = SecretProtection.UnprotectBytes(encrypted);
        try
        {
            using var cache = JsonDocument.Parse(plain);
            return ParseDesktop(cache.RootElement, root, account.ToString("D"), organization, now);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static byte[] DecryptChromium(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < 31 || !encrypted.AsSpan().StartsWith("v10"u8)) throw new CryptographicException();
        byte[] plain = new byte[encrypted.Length - 31];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(encrypted.Length - 16), plain);
        return plain;
    }

    internal static ClaudeCredential? ParseDesktop(JsonElement cache, string root, string account, string? organization, DateTimeOffset now)
    {
        if (cache.ValueKind != JsonValueKind.Object || !Guid.TryParse(account, out var accountId)) return null;
        var candidates = new List<(string Org, ClaudeCredential Credential, int Priority)>();
        string prefix = "acct:" + accountId.ToString("D") + "|";
        foreach (var entry in cache.EnumerateObject().Take(128))
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = entry.Name[prefix.Length..].Split(':', 3);
            const string host = "https://api.anthropic.com:";
            if (parts.Length != 3 || !Guid.TryParse(parts[0], out _) || !Guid.TryParse(parts[1], out var org) ||
                !parts[2].StartsWith(host, StringComparison.Ordinal)) continue;
            string orgId = org.ToString("D");
            if (organization is not null && !orgId.Equals(organization, StringComparison.OrdinalIgnoreCase)) continue;
            string[] scopes = parts[2][host.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!scopes.Contains("user:profile") || !scopes.Contains("user:inference")) continue;
            string? token = CodexContract.String(entry.Value, "token");
            if (!ValidToken(token) || !Integer(entry.Value, "expiresAt", out long expiry) || expiry <= 0) continue;
            string identity = Identity("desktop", root, accountId.ToString("D"), orgId);
            int priority = (expiry > now.AddSeconds(30).ToUnixTimeMilliseconds() ? 2 : 0) + (scopes.Contains("user:sessions:claude_code") ? 1 : 0);
            candidates.Add((orgId, new(identity, token!, expiry), priority));
        }
        // Without a matching live session, do not guess which organization is active.
        if (candidates.Select(c => c.Org).Distinct().Count() != 1) return null;
        return candidates.OrderByDescending(c => c.Priority).FirstOrDefault().Credential;
    }

    internal static ClaudeCredential? ParseCli(JsonElement root, string directory)
    {
        if (!CodexContract.Object(root, "claudeAiOauth", out var auth)) return null;
        string? token = CodexContract.String(auth, "accessToken");
        if (!ValidToken(token) || !Integer(auth, "expiresAt", out long expiry) || expiry <= 0) return null;
        // The token fingerprint deliberately invalidates readings after a CLI login
        // change when that file has no trustworthy account identifier.
        return new(Identity("cli", directory, token!), token!, expiry);
    }

    private static bool ValidToken(string? value) => value is { Length: > 0 and <= 8192 } && value.All(c => c is >= '!' and <= '~');
    private static string Identity(params string[] fields) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', fields))));
    private static bool Integer(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value);
    }
    private static bool SafeFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or CryptographicException or System.ComponentModel.Win32Exception;
}
