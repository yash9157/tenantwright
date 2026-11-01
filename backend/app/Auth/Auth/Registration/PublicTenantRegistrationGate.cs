using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Auth.Registration;

public interface IPublicTenantRegistrationGate
{
    Task<bool> IsAllowedAsync(string captchaToken, string email, string tenant, string? clientIp, CancellationToken cancellationToken = default);
}

public sealed class PublicTenantRegistrationGate : IPublicTenantRegistrationGate
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _valkey;
    private readonly IOptions<PublicTenantRegistrationOptions> _options;
    private readonly ILogger<PublicTenantRegistrationGate> _logger;

    public PublicTenantRegistrationGate(IHttpClientFactory httpClientFactory, IConnectionMultiplexer valkey, IOptions<PublicTenantRegistrationOptions> options, ILogger<PublicTenantRegistrationGate> logger)
        => (_httpClientFactory, _valkey, _options, _logger) = (httpClientFactory, valkey, options, logger);

    public async Task<bool> IsAllowedAsync(string captchaToken, string email, string tenant, string? clientIp, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(captchaToken) || !IsAllowedDomain(email, options)) return false;
        if (!await VerifyTurnstileAsync(captchaToken, clientIp, options, cancellationToken)) return false;
        try
        {
            var db = _valkey.GetDatabase();
            return await IncrementWithinLimitAsync(db, Hash(options, $"ip:{clientIp ?? "unknown"}"), options.IpHourlyLimit, TimeSpan.FromHours(1))
                && await IncrementWithinLimitAsync(db, Hash(options, $"email:{email.Trim().ToUpperInvariant()}"), options.EmailHourlyLimit, TimeSpan.FromHours(1))
                && await IncrementWithinLimitAsync(db, Hash(options, $"tenant:{tenant.Trim().ToUpperInvariant()}"), options.TenantDailyLimit, TimeSpan.FromDays(1));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Public tenant registration gate unavailable.");
            return false;
        }
    }

    private async Task<bool> VerifyTurnstileAsync(string token, string? clientIp, PublicTenantRegistrationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://challenges.cloudflare.com/turnstile/v0/siteverify")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["secret"] = options.TurnstileSecret, ["response"] = token, ["remoteip"] = clientIp ?? string.Empty }) };
            using var response = await _httpClientFactory.CreateClient("turnstile").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean()) return false;
            return string.IsNullOrWhiteSpace(options.ExpectedHostName) || (document.RootElement.TryGetProperty("hostname", out var host) && string.Equals(host.GetString(), options.ExpectedHostName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("Turnstile verification failed without accepting registration.");
            return false;
        }
    }

    private static async Task<bool> IncrementWithinLimitAsync(IDatabase db, string key, int limit, TimeSpan ttl)
    {
        var value = await db.StringIncrementAsync(key);
        if (value == 1) await db.KeyExpireAsync(key, ttl);
        return value <= Math.Clamp(limit, 1, 1000);
    }

    private static bool IsAllowedDomain(string email, PublicTenantRegistrationOptions options)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        var domain = email[(at + 1)..].Trim().ToUpperInvariant();
        return !options.BlockedDomains.Any(x => string.Equals(x.Trim(), domain, StringComparison.OrdinalIgnoreCase));
    }

    private static string Hash(PublicTenantRegistrationOptions options, string value)
        => "auth:public-registration:v1:" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.HashKey), Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
