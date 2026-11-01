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
using Shared;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public partial class IdentitySessionAppService
{
    public async Task<int> CountActiveByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (tenantId == Guid.Empty)
        {
            return 0;
        }

        return await unitOfWorkDbContext.IdentitySessions.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(session => session.TenantId == tenantId && session.Status == IdentitySessionStatus.Active, cancellationToken);
    }

    public async Task<List<IdentitySessionDto>> GetListByUserIdAsync(Guid userId, Guid? tenantId = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            return [];
        }

        List<IdentitySession> list;
        if (tenantId.HasValue)
        {
            if (tenantId.Value == Guid.Empty)
            {
                return [];
            }

            list = await unitOfWorkDbContext.IdentitySessions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.UserId == userId &&
                            x.TenantId == tenantId.Value &&
                            x.Status == IdentitySessionStatus.Active)
                .OrderByDescending(x => x.LastActiveTime)
                .ToListAsync();
        }
        else
        {
            var targetUser = await ResolveManagedTargetUserAsync(userId);
            list = await unitOfWorkDbContext.IdentitySessions
                .AsNoTracking()
                .Where(x => x.UserId == targetUser.Id &&
                            x.TenantId == targetUser.TenantId &&
                            x.Status == IdentitySessionStatus.Active)
                .OrderByDescending(x => x.LastActiveTime)
                .ToListAsync();
        }

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

    public async Task<PagedResultDto<IdentitySessionDto>> GetPagedListByUserIdAsync(Guid userId, GetUserSessionsPagedInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            return new PagedResultDto<IdentitySessionDto>();
        }

        input ??= new GetUserSessionsPagedInput();
        var targetUser = await ResolveManagedTargetUserAsync(userId);
        var query = unitOfWorkDbContext.IdentitySessions
            .AsNoTracking()
            .Where(x => x.UserId == targetUser.Id && x.TenantId == targetUser.TenantId);

        query = ApplyUserSessionFilters(query, input);
        // Rejects an undeclared field instead of silently falling back to the default order.
        query = ApplySorting(
            query,
            SortingValidator.ValidateAndNormalize<GetUserSessionsPagedInput>(input.Sorting));

        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var items = await query.Skip(skipCount).Take(maxResultCount).ToListAsync();

        var dtos = (await Task.WhenAll(items.Select(async session =>
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

        return new PagedResultDto<IdentitySessionDto>
        {
            TotalCount = totalCount,
            Items = dtos
        };
    }

    public async Task<PagedResultDto<OperationalSessionDto>> GetManagementPagedListAsync(GetOperationalSessionsInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new GetOperationalSessionsInput();
        EnsureSessionManagementActor();

        var query = BuildOperationalSessionQuery(unitOfWorkDbContext);

        var tenantNameFilter = input.TenantName?.Trim();
        IReadOnlyDictionary<Guid, string>? tenantNamesById = null;
        if (!string.IsNullOrWhiteSpace(tenantNameFilter) ||
            string.Equals(input.Sorting?.Trim(), "tenantname", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input.Sorting?.Trim(), "tenantname desc", StringComparison.OrdinalIgnoreCase))
        {
            tenantNamesById = await GetTenantNameLookupAsync();
        }

        if (_managementGuard.IsCurrentUserHostAdmin())
        {
            if (input.TenantId.HasValue)
            {
                query = query.Where(x => x.Session.TenantId == input.TenantId.Value);
            }

            if (!string.IsNullOrWhiteSpace(tenantNameFilter))
            {
                var matchingTenantIds = tenantNamesById!
                    .Where(x => x.Value.Contains(tenantNameFilter, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Key)
                    .ToList();
                query = query.Where(x => x.Session.TenantId.HasValue && matchingTenantIds.Contains(x.Session.TenantId.Value));
            }
        }

        if (input.UserId.HasValue)
        {
            query = query.Where(x => x.User.Id == input.UserId.Value);
        }

        var userFilter = input.UserFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(userFilter))
        {
            query = query.Where(x =>
                (x.User.UserName != null && x.User.UserName.Contains(userFilter)) ||
                (x.User.Email != null && x.User.Email.Contains(userFilter)));
        }

        var clientId = input.ClientId?.Trim();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            query = query.Where(x => x.Session.ClientId != null && x.Session.ClientId.Contains(clientId));
        }

        var deviceFilter = input.DeviceFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(deviceFilter))
        {
            query = query.Where(x =>
                (x.Session.DeviceId != null && x.Session.DeviceId.Contains(deviceFilter)) ||
                (x.Session.UserAgent != null && x.Session.UserAgent.Contains(deviceFilter)));
        }

        var ipFilter = input.IpFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(ipFilter))
        {
            query = query.Where(x =>
                (x.Session.IpAddress != null && x.Session.IpAddress.Contains(ipFilter)));
        }

        if (TryParseStatus(input.Status, out var sessionStatus) && sessionStatus.HasValue)
        {
            var parsedStatus = sessionStatus.Value;
            query = query.Where(x => x.Session.Status == parsedStatus);
        }

        if (input.CreatedFromUtc.HasValue)
        {
            query = query.Where(x => x.Session.CreationTime >= input.CreatedFromUtc.Value);
        }

        if (input.CreatedToUtc.HasValue)
        {
            query = query.Where(x => x.Session.CreationTime <= input.CreatedToUtc.Value);
        }

        if (input.LastActiveFromUtc.HasValue)
        {
            query = query.Where(x => x.Session.LastActiveTime >= input.LastActiveFromUtc.Value);
        }

        if (input.LastActiveToUtc.HasValue)
        {
            query = query.Where(x => x.Session.LastActiveTime <= input.LastActiveToUtc.Value);
        }

        if (input.RevokedFromUtc.HasValue)
        {
            query = query.Where(x => x.Session.RevokedAtUtc.HasValue && x.Session.RevokedAtUtc.Value >= input.RevokedFromUtc.Value);
        }

        if (input.RevokedToUtc.HasValue)
        {
            query = query.Where(x => x.Session.RevokedAtUtc.HasValue && x.Session.RevokedAtUtc.Value <= input.RevokedToUtc.Value);
        }

        // Rejects an undeclared field instead of silently falling back to the default order.
        query = ApplyOperationalSorting(
            query,
            SortingValidator.ValidateAndNormalize<GetOperationalSessionsInput>(input.Sorting));

        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var items = await query.Skip(skipCount).Take(maxResultCount).ToListAsync();
        tenantNamesById ??= await GetTenantNameLookupAsync();

        var dtos = (await Task.WhenAll(items.Select(async item =>
        {
            var dto = MapToOperationalDto(item, tenantNamesById);
            dto.Status = await ComputeEffectiveStatusAsync(
                item.Session.Status,
                item.Session.CreationTime,
                item.Session.TenantId,
                item.Session.UserId,
                item.Session.SessionId);
            return dto;
        }))).ToList();

        return new PagedResultDto<OperationalSessionDto>
        {
            TotalCount = totalCount,
            Items = dtos
        };
    }

    [Audited]
    public async Task<BulkRevokeSessionsResultDto> RevokeManagedAsync(Guid sessionId, ManagedSessionRevokeInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        ArgumentNullException.ThrowIfNull(input);
        if (sessionId == Guid.Empty)
        {
            throw new SharedValidationException("A session identifier is required.");
        }

        var reason = input.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
        {
            throw new SharedValidationException("Reason is required and must not exceed 512 characters.");
        }

        EnsureSessionManagementActor();
        var scopedSessions =
            from session in unitOfWorkDbContext.IdentitySessions.AsNoTracking()
            join user in unitOfWorkDbContext.Users.AsNoTracking() on session.UserId equals user.Id
            where session.Id == sessionId &&
                  session.TenantId.HasValue &&
                  user.TenantId == session.TenantId
            select new { session.TenantId, session.UserId };

        var isInCallerScope = _managementGuard.IsCurrentUserHostAdmin();
        if (isInCallerScope)
        {
            isInCallerScope = await scopedSessions.AnyAsync();
        }
        else
        {
            var tenantId = _managementGuard.RequireTenantId();
            var elevatedUserIds = ElevatedUserIdsQuery(unitOfWorkDbContext, tenantId);
            isInCallerScope = await scopedSessions.AnyAsync(session =>
                session.TenantId == tenantId && !elevatedUserIds.Contains(session.UserId));
        }
        if (!isInCallerScope)
        {
            throw new UnauthorizedAccessException("Session revoke exceeded the caller scope.");
        }

        return await _sessionRevocationService.RevokeSelectedAsync(
            [sessionId],
            action: "LogoutManagedSessionByAdmin",
            revocationSource: "ManagedSessionRevoke",
            revokedByUserId: _currentUser.Id,
            revocationReason: reason);
    }

    [Audited]
    public async Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(BulkRevokeSessionsInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new BulkRevokeSessionsInput();
        EnsureSessionManagementActor();

        var requestedIds = input.SessionIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return new BulkRevokeSessionsResultDto();
        }

        var scopedSessionIds = await BuildOperationalSessionQuery(unitOfWorkDbContext)
            .Where(x => requestedIds.Contains(x.Session.Id))
            .Select(x => x.Session.Id)
            .ToListAsync();

        if (scopedSessionIds.Count != requestedIds.Count)
        {
            throw new UnauthorizedAccessException("Selected session revoke exceeded the caller scope.");
        }

        return await _sessionRevocationService.RevokeSelectedAsync(
            requestedIds,
            action: "LogoutSelectedSessionsByAdmin",
            revocationSource: "BulkRevoke",
            revokedByUserId: _currentUser.Id,
            revocationReason: input.Reason);
    }

    [Audited]
    public async Task<int> RevokeAllByTenantIdAsync(Guid tenantId, string? revocationReason = null)
    {
        if (tenantId == Guid.Empty)
        {
            return 0;
        }

        if (!_managementGuard.IsCurrentUserHostAdmin())
        {
            throw new UnauthorizedAccessException("Tenant-wide session revoke requires host admin scope.");
        }

        return await _sessionRevocationService.RevokeAllByTenantAsync(
            tenantId,
            action: "LogoutAllSessionsByAdmin",
            revocationSource: "TenantWideRevoke",
            revokedByUserId: _currentUser.Id,
            revocationReason: NormalizeManagedBulkRevokeReason(revocationReason));
    }

}
