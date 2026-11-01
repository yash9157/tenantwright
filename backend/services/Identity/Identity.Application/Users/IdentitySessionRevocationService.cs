using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Identity.Application.Account;
using System.Threading;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public sealed class IdentitySessionRevocationService : IIdentitySessionRevocationService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly IDistributedCache<IdentitySessionValidityCacheItem> _sessionValidityCache;
    private readonly ISessionMarkerStore _sessionMarkerStore;
    private readonly ITenantWrightMessageSender _eventBus;
    private readonly IdentityUserSecurityAlertPublisher _securityAlertPublisher;

    public IdentitySessionRevocationService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, IDistributedCache<IdentitySessionValidityCacheItem> sessionValidityCache, ISessionMarkerStore sessionMarkerStore, ITenantWrightMessageSender eventBus, IdentityUserSecurityAlertPublisher securityAlertPublisher)
    {
        _dbContextProvider = dbContextProvider;
        _sessionValidityCache = sessionValidityCache;
        _sessionMarkerStore = sessionMarkerStore;
        _eventBus = eventBus;
        _securityAlertPublisher = securityAlertPublisher;
    }

    public async Task<int> RevokeAllAsync(
        Guid userId,
        Guid? tenantId,
        string action,
        string? revocationSource = null,
        Guid? revokedByUserId = null,
        string? revocationReason = null,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            return 0;
        }

        var query = unitOfWorkDbContext.IdentitySessions
            .IgnoreQueryFilters()
            .Where(x => x.UserId == userId);
        query = tenantId.HasValue
            ? query.Where(x => x.TenantId == tenantId.Value)
            : query.Where(x => x.TenantId == null);

        var sessions = await query.ToListAsync(cancellationToken);
        var result = await RevokeSessionsInternalAsync(
            sessions,
            action,
            revocationSource ?? action,
            revokedByUserId,
            revocationReason,
            cancellationToken);

        return result.RevokedCount;
    }

    public async Task<int> RevokeAllByTenantAsync(
        Guid tenantId,
        string action,
        string? revocationSource = null,
        Guid? revokedByUserId = null,
        string? revocationReason = null,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (tenantId == Guid.Empty)
        {
            return 0;
        }

        var sessions = await unitOfWorkDbContext.IdentitySessions
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.Status == IdentitySessionStatus.Active)
            .ToListAsync(cancellationToken);

        var result = await RevokeSessionsInternalAsync(
            sessions,
            action,
            revocationSource ?? action,
            revokedByUserId,
            revocationReason,
            cancellationToken);

        return result.RevokedCount;
    }

    public async Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(
        IReadOnlyCollection<Guid> sessionIds,
        string action,
        string? revocationSource = null,
        Guid? revokedByUserId = null,
        string? revocationReason = null,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (sessionIds == null || sessionIds.Count == 0)
        {
            return new BulkRevokeSessionsResultDto();
        }

        var normalizedIds = sessionIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (normalizedIds.Count == 0)
        {
            return new BulkRevokeSessionsResultDto();
        }

        var sessions = await unitOfWorkDbContext.IdentitySessions
            .IgnoreQueryFilters()
            .Where(x => normalizedIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var result = await RevokeSessionsInternalAsync(
            sessions,
            action,
            revocationSource ?? action,
            revokedByUserId,
            revocationReason,
            cancellationToken);
        result.RequestedCount = normalizedIds.Count;
        return result;
    }

    private async Task<BulkRevokeSessionsResultDto> RevokeSessionsInternalAsync(
        List<IdentitySession> sessions,
        string action,
        string revocationSource,
        Guid? revokedByUserId,
        string? revocationReason,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (sessions.Count == 0)
        {
            return new BulkRevokeSessionsResultDto();
        }

        var userIds = sessions.Select(x => x.UserId).Distinct().ToList();
        var userDirectory = await unitOfWorkDbContext.Users
            .IgnoreQueryFilters()
            .Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(
                x => x.Id,
                x => new UserDirectoryEntry
                {
                    UserName = x.UserName,
                    Email = x.Email
                },
                cancellationToken);

        var requestedCount = sessions.Count;
        var alreadyRevokedCount = 0;
        var revokedCount = 0;
        var occurredAtUtc = DateTime.UtcNow;
        var affectedUsers = new HashSet<(Guid? TenantId, Guid UserId)>();
        var revokedSessions = new List<IdentitySession>();

        foreach (var session in sessions)
        {
            if (session.Status == IdentitySessionStatus.Revoked)
            {
                alreadyRevokedCount++;
                continue;
            }

            session.Status = IdentitySessionStatus.Revoked;
            session.RevokedAtUtc = occurredAtUtc;
            session.RevokedByUserId = revokedByUserId;
            session.RevocationSource = revocationSource;
            session.RevocationReason = string.IsNullOrWhiteSpace(revocationReason) ? null : revocationReason.Trim();

            await _sessionValidityCache.RemoveAsync(
                IdentitySessionCache.ValidKey(session.UserId, session.SessionId),
                token: cancellationToken);
            await _sessionMarkerStore.RemoveAsync(session.TenantId, session.UserId, session.SessionId, cancellationToken);

            await unitOfWorkDbContext.AddAsync(new IdentitySecurityLog
            {
                Id = Guid.CreateVersion7(),
                TenantId = session.TenantId,
                ApplicationName = "Identity",
                Identity = "Session",
                Action = action,
                SessionId = session.SessionId,
                UserId = session.UserId,
                UserName = userDirectory.GetValueOrDefault(session.UserId)?.UserName,
                ClientId = session.ClientId,
                ClientIpAddress = session.IpAddress,
                BrowserInfo = session.UserAgent,
                CreationTime = occurredAtUtc,
                ExtraProperties = JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["revocationSource"] = revocationSource,
                    ["revocationReason"] = session.RevocationReason,
                    ["revokedByUserId"] = revokedByUserId?.ToString("D")
                })
            }, cancellationToken);

            revokedCount++;
            affectedUsers.Add((session.TenantId, session.UserId));
            revokedSessions.Add(session);
        }

        if (revokedCount > 0)
        {
            foreach (var session in revokedSessions)
            {
                if (session.TenantId.HasValue && session.TenantId.Value != Guid.Empty)
                {
                    await _eventBus.PublishEventAsync(
                        new IdentitySessionRevokedTenantEto
                        {
                            TenantId = session.TenantId.Value,
                            UserId = session.UserId,
                            SessionId = session.SessionId,
                            Action = action ?? string.Empty,
                            OccurredAtUtc = occurredAtUtc
                        },
                        cancellationToken);
                }
                else
                {
                    await _eventBus.PublishEventAsync(
                        new IdentitySessionRevokedHostEto
                        {
                            UserId = session.UserId,
                            SessionId = session.SessionId,
                            Action = action ?? string.Empty,
                            OccurredAtUtc = occurredAtUtc
                        },
                        cancellationToken);
                }
            }

            foreach (var (tenantId, userId) in affectedUsers)
            {
                if (revokedByUserId.HasValue &&
                    userDirectory.TryGetValue(userId, out var userEntry))
                {
                    await _securityAlertPublisher.PublishAdminSessionRevokedAlertAsync(
                        tenantId,
                        userId,
                        userEntry.UserName,
                        userEntry.Email,
                        action,
                        revocationReason,
                        occurredAtUtc);
                }
            }

            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        }

        return new BulkRevokeSessionsResultDto
        {
            RequestedCount = requestedCount,
            RevokedCount = revokedCount,
            AlreadyRevokedCount = alreadyRevokedCount
        };
    }

    private sealed class UserDirectoryEntry
    {
        public string? UserName { get; init; }
        public string? Email { get; init; }
    }
}
