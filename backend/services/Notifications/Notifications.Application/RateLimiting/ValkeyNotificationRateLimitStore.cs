using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.RateLimiting;
using Shared.Modularity.DependencyInjection;
using StackExchange.Redis;

namespace Notifications.Application.RateLimiting;

/// <summary>One atomic reservation covers both budgets. All keys share one deliberate cluster slot.</summary>
public sealed class ValkeyNotificationRateLimitStore(IEnumerable<IConnectionMultiplexer> connections,
    IOptions<RedisCacheOptions> cacheOptions) : INotificationRateLimitStore, ITransientDependency
{
    // All accessed keys are declared in KEYS. Server time prevents replica clock skew.
    // A denied operation binds its identity but never consumes either budget.
    private const string ReservationScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1])
        local window = tonumber(ARGV[3])
        local bucket = math.floor(now / window)
        local retryAt = (bucket + 1) * window
        local binding = redis.call('HGET', KEYS[1], 'binding')
        if binding and binding ~= ARGV[1] then return {4, 0, 0, retryAt} end
        if redis.call('HGET', KEYS[1], 'granted') == '1' then return {1, 0, 0, retryAt} end
        local amount = tonumber(ARGV[2])
        local field = tostring(window) .. ':' .. tostring(bucket)
        local tenantCount = tonumber(redis.call('HGET', KEYS[2], field) or '0')
        local globalCount = tonumber(redis.call('HGET', KEYS[3], field) or '0')
        redis.call('HSET', KEYS[1], 'binding', ARGV[1])
        redis.call('EXPIRE', KEYS[1], 2678400)
        if tonumber(ARGV[4]) > 0 and tenantCount + amount > tonumber(ARGV[4]) then
            return {2, tenantCount, globalCount, retryAt}
        end
        if tonumber(ARGV[5]) > 0 and globalCount + amount > tonumber(ARGV[5]) then
            return {3, tenantCount, globalCount, retryAt}
        end
        local previous = tostring(window) .. ':' .. tostring(bucket - 1)
        for _, key in ipairs({KEYS[2], KEYS[3]}) do
            for _, old in ipairs(redis.call('HKEYS', key)) do
                if old ~= field and old ~= previous then redis.call('HDEL', key, old) end
            end
        end
        redis.call('HINCRBY', KEYS[2], field, amount)
        redis.call('HINCRBY', KEYS[3], field, amount)
        redis.call('EXPIRE', KEYS[2], window * 2)
        redis.call('EXPIRE', KEYS[3], window * 2)
        redis.call('HSET', KEYS[1], 'granted', '1')
        return {1, tenantCount + amount, globalCount + amount, retryAt}
        """;

    public async Task<NotificationQuotaReservation> ReserveAsync(NotificationQuotaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.TenantId == Guid.Empty || !Enum.IsDefined(request.Channel) ||
            request.Amount is < 1 or > 1000 || request.WindowSeconds is < 1 or > 3600 || request.TenantLimit < 0 || request.GlobalLimit < 0)
            throw new ArgumentException("Invalid notification quota reservation.", nameof(request));
        var connection = connections.SingleOrDefault()
            ?? throw new InvalidOperationException("Atomic notification quotas require the configured Valkey connection.");
        var instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheOptions.Value.InstanceName ?? "")))[..16];
        var prefix = $"{{tenantwright-notification-quotas:{instance}}}:";
        var scope = request.TenantId?.ToString("N") ?? "host";
        var binding = string.Create(CultureInfo.InvariantCulture, $"{scope}:{(int)request.Channel}:{request.Amount}");
        var response = await connection.GetDatabase().ScriptEvaluateAsync(ReservationScript,
            [prefix + $"reservation:{scope}:{request.OperationId:N}", prefix + $"tenant:{scope}:{(int)request.Channel}", prefix + "global"],
            [binding, request.Amount, request.WindowSeconds, request.TenantLimit, request.GlobalLimit])
            .WaitAsync(cancellationToken);
        var values = (RedisResult[])response!;
        return new NotificationQuotaReservation((NotificationQuotaDecision)(int)values[0], (long)values[1],
            (long)values[2], DateTimeOffset.FromUnixTimeSeconds((long)values[3]).UtcDateTime);
    }
}
