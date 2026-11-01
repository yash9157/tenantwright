using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Mints a short-lived Google OAuth2 access token from a Firebase/GCP service-account credential
/// using the JWT-bearer grant (RFC 7523), pure BCL RS256 signing — no external JWT/Google auth
/// package. A fresh token is minted per send; this is correct but not optimized (no cross-call
/// caching), acceptable at tenantwright admin-console push volumes.
/// </summary>
public sealed class FcmAccessTokenProvider : ITransientDependency
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string GrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public FcmAccessTokenProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAccessTokenAsync(string clientEmail, string privateKeyPem, CancellationToken cancellationToken)
    {
        var assertion = CreateSignedJwt(clientEmail, privateKeyPem);

        var client = _httpClientFactory.CreateClient(FcmPushProviderAdapter.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = GrantType,
                ["assertion"] = assertion
            })
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"FCM OAuth2 token exchange failed (HTTP {(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
        {
            throw new InvalidOperationException("FCM OAuth2 token response did not contain an access_token.");
        }

        return tokenElement.GetString()
            ?? throw new InvalidOperationException("FCM OAuth2 token response contained a null access_token.");
    }

    private static string CreateSignedJwt(string clientEmail, string privateKeyPem)
    {
        var now = DateTimeOffset.UtcNow;
        var header = new { alg = "RS256", typ = "JWT" };
        var claims = new
        {
            iss = clientEmail,
            scope = MessagingScope,
            aud = TokenEndpoint,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddHours(1).ToUnixTimeSeconds()
        };

        var headerB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));
        var claimsB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims, JsonOptions));
        var unsigned = $"{headerB64}.{claimsB64}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{unsigned}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
