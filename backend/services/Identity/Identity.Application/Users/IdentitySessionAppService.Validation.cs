using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Shared.Caching.Abstractions;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public partial class IdentitySessionAppService
{
    public Task<bool> ValidateAsync(Guid userId, string sessionId, Guid? tenantId = null)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult(false);
        }

        return ValidateStrictAsync(userId, sessionId, tenantId);
    }

    private async Task<bool> ValidateStrictAsync(Guid userId, string sessionId, Guid? tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var cacheKey = IdentitySessionCache.ValidKey(userId, sessionId);
        var cached = await _sessionValidityCache.GetAsync(cacheKey);
        if (cached is { IsValid: false })
        {
            return false;
        }

        var sessionGuid = TryParseSessionId(sessionId);
        var query = unitOfWorkDbContext.IdentitySessions
            .Where(x => x.UserId == userId && x.Status == IdentitySessionStatus.Active);

        query = tenantId.HasValue
            ? query.Where(x => x.TenantId == tenantId.Value)
            : query.Where(x => x.TenantId == null);

        query = sessionGuid.HasValue
            ? query.Where(x => x.Id == sessionGuid.Value || x.SessionId == sessionId)
            : query.Where(x => x.SessionId == sessionId);

        var session = await query.OrderByDescending(x => x.LastActiveTime).FirstOrDefaultAsync();
        if (session == null)
        {
            await _sessionValidityCache.SetAsync(
                cacheKey,
                new IdentitySessionValidityCacheItem { IsValid = false },
                options: new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = NegativeCacheDuration });
            return false;
        }

        await _sessionValidityCache.RemoveAsync(cacheKey);

        var now = DateTime.UtcNow;
        if (session.LastActiveTime < now.Subtract(LastActiveUpdateInterval))
        {
            session.LastActiveTime = now;
            await unitOfWorkDbContext.SaveChangesAsync();
        }

        return true;
    }

}
