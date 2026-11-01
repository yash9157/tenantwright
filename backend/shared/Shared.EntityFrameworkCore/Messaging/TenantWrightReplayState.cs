using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.AuditLogs.Auditing;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

[DisableAuditing]
public sealed class TenantWrightReplayState
{
    public string ReplayId { get; private set; } = string.Empty;
    public string Destination { get; private set; } = string.Empty;
    public string AuditId { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    private TenantWrightReplayState()
    {
    }

    public TenantWrightReplayState(ProtectedReplayIssue issue)
    {
        ReplayId = issue.ReplayId;
        Destination = issue.Destination;
        AuditId = issue.AuditId;
        ExpiresAt = issue.ExpiresAt;
    }
}

/// <summary>
/// Replay idempotency keys for operator-issued protected replays. Shared-database-only by design: the injected
/// scoped <typeparamref name="TDbContext"/> is always bound to the host database, so every replay issued by this host
/// is deduplicated in one place regardless of the replayed message's tenant. The rows carry no tenant data.
/// </summary>
public sealed class EfProtectedReplayIdempotencyStore<TDbContext>(TDbContext dbContext)
    : IProtectedReplayIdempotencyStore
    where TDbContext : DbContext
{
    public async Task<bool> TryIssueAsync(ProtectedReplayIssue issue, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue.ReplayId);
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."MessagingReplayState"
                ("ReplayId", "Destination", "AuditId", "ExpiresAt", "ConsumedAt")
            VALUES
                ({issue.ReplayId}, {issue.Destination}, {issue.AuditId}, {issue.ExpiresAt}, NULL)
            ON CONFLICT ("ReplayId") DO NOTHING
            """, cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryConsumeIssuedAsync(
        ProtectedReplayConsumption consumption,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumption.ReplayId);
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public."MessagingReplayState"
            SET "ConsumedAt" = {consumption.ConsumedAt}
            WHERE "ReplayId" = {consumption.ReplayId}
              AND "Destination" = {consumption.Destination}
              AND "AuditId" = {consumption.AuditId}
              AND "ExpiresAt" = {consumption.ExpiresAt}
              AND "ExpiresAt" > {consumption.ConsumedAt}
              AND "ConsumedAt" IS NULL
            """, cancellationToken);
        return affected == 1;
    }
}

public static class TenantWrightReplayStateRegistration
{
    public static IServiceCollection AddTenantWrightProtectedReplayIdempotencyStore<TDbContext>(
        this IServiceCollection services)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IProtectedReplayIdempotencyStore, EfProtectedReplayIdempotencyStore<TDbContext>>();
        return services;
    }
}
