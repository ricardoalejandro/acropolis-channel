using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Acropolis.Identity.Application;

namespace Acropolis.TestSupport;

// Synthetic QA secrets stay in this process. No endpoint retrieves an active key.
public static class MfaTestClient
{
    private static readonly ConcurrentDictionary<string, string> Keys = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> Used = new();
    public static async Task<HttpResponseMessage> Write<T>(HttpClient client, string route, T body, CancellationToken token)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/" + route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, token);
    }
    public static async Task<HttpResponseMessage> Login(HttpClient client, string email, string password, CancellationToken token)
    {
        var login = await Write(client, "login", new LoginRequest(email, password), token);
        if (login.StatusCode != HttpStatusCode.Accepted) return login;
        var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeView>(token))!;
        login.Dispose();
        if (challenge.EnrollmentRequired)
        {
            using var start = await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(ChallengeToken: challenge.ChallengeToken), token);
            start.EnsureSuccessStatusCode();
            var enrollment = (await start.Content.ReadFromJsonAsync<MfaEnrollmentView>(token))!;
            Keys[email] = enrollment.SharedKey;
            var enabled = await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, FreshCode(enrollment.SharedKey)), token);
            enabled.EnsureSuccessStatusCode();
            var body = await enabled.Content.ReadFromJsonAsync<JsonElement>(token);
            enabled.Content = JsonContent.Create(body.GetProperty("user"));
            return enabled;
        }
        if (!Keys.TryGetValue(email, out var key)) throw new InvalidOperationException("QA has no authenticator fixture for this account.");
        return await Write(client, "mfa/challenge", new MfaVerifyRequest(challenge.ChallengeToken, Code: FreshCode(key)), token);
    }
    public static string FreshCode(string key)
    {
        var used = Used.GetOrAdd(key, _ => []);
        lock (used)
        {
            var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            foreach (var offset in new[] { 0, 1, 2, -1, -2 })
            {
                var code = Code(key, step + offset);
                if (used.Add(code)) return code;
            }
        }
        throw new InvalidOperationException("QA exhausted the provider's real TOTP time window; use a recovery fixture or wait.");
    }
    public static string Code(string key, long step)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); int bits = 0, value = 0;
        foreach (var character in key)
        {
            value = (value << 5) | alphabet.IndexOf(character); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(value >> bits)); }
        }
        Span<byte> counter = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter); var offset = hash[^1] & 15;
        var number = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (number % 1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
