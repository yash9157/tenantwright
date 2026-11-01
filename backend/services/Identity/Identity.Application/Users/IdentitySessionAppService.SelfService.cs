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
    public async Task<List<IdentitySessionDto>> GetListAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userId = _currentUser.Id;
        if (!userId.HasValue)
        {
            return [];
        }

        var list = await ApplyCurrentTenantScope(unitOfWorkDbContext.IdentitySessions.AsNoTracking())
            .Where(x => x.UserId == userId.Value && x.Status == IdentitySessionStatus.Active)
            .OrderByDescending(x => x.LastActiveTime)
            .ToListAsync();

        var dtos = (await Task.WhenAll(list.Select(async session =>
        {
            var dto = MapToDto(session);
            dto.Status = await ComputeEffectiveStatusAsync(
                session.Status,
                session.CreationTime,
                session.TenantId,
                session.UserId,
                session.SessionId);
            return dto;
        }))).ToList();
        return dtos;
    }

    [Audited]
    public async Task RevokeAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userId = _currentUser.Id;
        if (!userId.HasValue)
        {
            return;
        }

        var session = await ApplyCurrentTenantScope(unitOfWorkDbContext.IdentitySessions)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId.Value && x.Status == IdentitySessionStatus.Active);
        if (session == null)
        {
            return;
        }

        await RevokeKnownSessionsAsync([session], "Logout", "SelfService");
    }

    [Audited]
    public Task RevokeCurrentBySessionIdAsync(string sessionId)
    {
        var userId = _currentUser.Id;
        if (!userId.HasValue)
        {
            return Task.CompletedTask;
        }

        return RevokeBySessionIdAsync(userId.Value, sessionId);
    }

    public async Task RevokeBySessionIdAsync(Guid userId, string sessionId, Guid? tenantId = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var sessionGuid = TryParseSessionId(sessionId);
        var query = ApplySessionTenantScope(unitOfWorkDbContext.IdentitySessions, tenantId)
            .Where(x => x.UserId == userId && x.Status == IdentitySessionStatus.Active);

        if (sessionGuid.HasValue)
        {
            query = query.Where(x => x.Id == sessionGuid.Value || x.SessionId == sessionId);
        }
        else
        {
            query = query.Where(x => x.SessionId == sessionId);
        }

        var sessions = await query.ToListAsync();
        if (sessions.Count == 0)
        {
            return;
        }

        await RevokeKnownSessionsAsync(sessions, "Logout", "SelfService");
    }

    public Task<int> RevokeAllByUserIdAsync(Guid userId, string? revocationReason = null)
    {
        if (userId == Guid.Empty)
        {
            return Task.FromResult(0);
        }

        return RevokeAllManagedByUserIdAsync(userId, revocationReason);
    }

    public async Task<int> RevokeOthersAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userId = _currentUser.Id;
        if (!userId.HasValue)
        {
            return 0;
        }

        var currentSessionId = _currentUser.SessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return 0;
        }

        var sessionGuid = TryParseSessionId(currentSessionId);
        var query = ApplyCurrentTenantScope(unitOfWorkDbContext.IdentitySessions)
            .Where(x => x.UserId == userId.Value && x.Status == IdentitySessionStatus.Active);

        if (sessionGuid.HasValue)
        {
            query = query.Where(x => x.Id != sessionGuid.Value && x.SessionId != currentSessionId);
        }
        else
        {
            query = query.Where(x => x.SessionId != currentSessionId);
        }

        var sessions = await query.ToListAsync();
        if (sessions.Count == 0)
        {
            return 0;
        }

        return (await RevokeKnownSessionsAsync(sessions, "LogoutOtherSessions", "SelfService")).RevokedCount;
    }

}
