using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.SecurityLogs;
using Identity.Domain.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.SecurityLogs;

public class SecurityLogAppService : ISecurityLogAppService, ITransientDependency
{
    private static readonly string[] ElevatedRoleNames =
    [
        TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant(),
        TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
    ];

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;

    public SecurityLogAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, ICurrentUser currentUser)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
    }

    public async Task<SecurityLogDto> GetAsync(Guid id)
    {
        return MapToDto(await GetScopedLogAsync(id));
    }

    public async Task<SecurityLogDetailDto> GetDetailAsync(Guid id)
    {
        var log = await GetScopedLogAsync(id);
        var summary = MapToDto(log);
        return new SecurityLogDetailDto
        {
            Id = summary.Id,
            ApplicationName = summary.ApplicationName,
            Identity = summary.Identity,
            Action = summary.Action,
            UserId = summary.UserId,
            UserName = summary.UserName,
            ClientId = summary.ClientId,
            ClientIpAddress = summary.ClientIpAddress,
            CreationTime = summary.CreationTime,
            RiskLevel = summary.RiskLevel,
            RiskAction = summary.RiskAction,
            RiskScore = summary.RiskScore,
            RiskReasons = summary.RiskReasons,
            BlockedByRisk = summary.BlockedByRisk,
            FalsePositive = summary.FalsePositive,
            CountryCode = summary.CountryCode,
            RiskOverrideId = summary.RiskOverrideId,
            SessionId = log.SessionId,
            CorrelationId = log.CorrelationId,
            BrowserInfo = log.BrowserInfo,
            Location = log.Location
        };
    }

    public async Task<List<SecurityLogDto>> GetListAsync(string? userName = null, string? clientId = null)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = ApplyCurrentTenantScope(unitOfWorkDbContext, unitOfWorkDbContext.IdentitySecurityLogs.AsNoTracking());

        if (!string.IsNullOrEmpty(userName)) query = query.Where(x => x.UserName == userName);
        if (!string.IsNullOrEmpty(clientId)) query = query.Where(x => x.ClientId == clientId);

        var list = await query.OrderByDescending(x => x.CreationTime).Take(100).ToListAsync();
        return list.Select(MapToDto).ToList();
    }

    public async Task<PagedResultDto<SecurityLogDto>> GetPagedListAsync(SecurityLogListInput input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        input ??= new SecurityLogListInput();

        var query = ApplyCurrentTenantScope(unitOfWorkDbContext, unitOfWorkDbContext.IdentitySecurityLogs.AsNoTracking().AsQueryable());

        if (!string.IsNullOrWhiteSpace(input.UserName))
        {
            query = query.Where(x => x.UserName == input.UserName);
        }

        if (!string.IsNullOrWhiteSpace(input.ClientId))
        {
            query = query.Where(x => x.ClientId == input.ClientId);
        }

        if (!string.IsNullOrWhiteSpace(input.Action))
        {
            query = query.Where(x => x.Action == input.Action);
        }

        if (!string.IsNullOrWhiteSpace(input.RiskLevel))
        {
            query = query.Where(x => x.RiskLevel == input.RiskLevel);
        }

        if (!string.IsNullOrWhiteSpace(input.RiskAction))
        {
            query = query.Where(x => x.RiskAction == input.RiskAction);
        }

        if (input.BlockedByRisk.HasValue)
        {
            query = query.Where(x => x.BlockedByRisk == input.BlockedByRisk.Value);
        }

        if (input.FalsePositive.HasValue)
        {
            query = query.Where(x => x.FalsePositive == input.FalsePositive.Value);
        }

        if (!string.IsNullOrWhiteSpace(input.CountryCode))
        {
            var normalizedCountryCode = input.CountryCode.Trim().ToUpperInvariant();
            query = query.Where(x => x.CountryCode == normalizedCountryCode);
        }

        if (!string.IsNullOrWhiteSpace(input.ClientIpAddress))
        {
            query = query.Where(x => x.ClientIpAddress == input.ClientIpAddress);
        }

        if (!string.IsNullOrWhiteSpace(input.ApplicationName))
        {
            query = query.Where(x => x.ApplicationName == input.ApplicationName);
        }

        if (!string.IsNullOrWhiteSpace(input.Identity))
        {
            query = query.Where(x => x.Identity == input.Identity);
        }

        if (input.UserId.HasValue && input.UserId.Value != Guid.Empty)
        {
            query = query.Where(x => x.UserId == input.UserId.Value);
        }

        if (input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value);
        }

        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);

        var items = await query
            // Rejects an undeclared field instead of silently falling back to newest-first. The mapping
            // below is already direction-complete, so only the whitelist was missing.
            .ApplySorting(SortingValidator.ValidateAndNormalize<SecurityLogListInput>(input.Sorting))
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();

        return new PagedResultDto<SecurityLogDto>
        {
            TotalCount = totalCount,
            Items = items.Select(MapToDto).ToList()
        };
    }

    private IQueryable<IdentitySecurityLog> ApplyCurrentTenantScope(IdentityDbContextApp unitOfWorkDbContext, IQueryable<IdentitySecurityLog> query)
    {
        if (_currentTenant.Id.HasValue)
        {
            query = query.Where(x => x.TenantId == _currentTenant.Id.Value);
            if (_currentUser.Roles.Any(TenantWrightIdentityRoleNames.IsTenantAdminRole) &&
                !_currentUser.IsInRole(TenantWrightIdentityRoleNames.HostAdmin))
            {
                var elevatedUserIds = BuildElevatedUserIdsQuery(unitOfWorkDbContext, _currentTenant.Id.Value);
                query = query.Where(x => !x.UserId.HasValue || !elevatedUserIds.Contains(x.UserId.Value));
            }

            return query;
        }

        if (_currentUser.IsAuthenticated)
        {
            if (_currentUser.IsInRole(TenantWrightIdentityRoleNames.HostAdmin))
            {
                return query.IgnoreQueryFilters();
            }

            return query.Where(x => x.TenantId == null);
        }

        return query.Where(x => false);
    }

    private async Task<IdentitySecurityLog> GetScopedLogAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var log = await ApplyCurrentTenantScope(unitOfWorkDbContext, unitOfWorkDbContext.IdentitySecurityLogs.AsNoTracking())
                    .FirstOrDefaultAsync(x => x.Id == id);
        return log ?? throw new EntityNotFoundException<IdentitySecurityLog>(id);
    }

    private static IQueryable<Guid> BuildElevatedUserIdsQuery(IdentityDbContextApp unitOfWorkDbContext, Guid tenantId)
    {
        return
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
            where user.TenantId == tenantId && ElevatedRoleNames.Contains(role.NormalizedName ?? string.Empty)
            select userRole.UserId;
    }

    private SecurityLogDto MapToDto(IdentitySecurityLog log)
    {
        var riskSnapshot = IdentitySecurityLogRiskMetadata.Read(log);

        return new SecurityLogDto
        {
            Id = log.Id,
            ApplicationName = log.ApplicationName,
            Identity = log.Identity,
            Action = log.Action,
            UserId = log.UserId,
            UserName = log.UserName,
            ClientId = log.ClientId,
            ClientIpAddress = log.ClientIpAddress,
            CreationTime = log.CreationTime,
            RiskLevel = riskSnapshot.RiskLevel,
            RiskAction = riskSnapshot.RiskAction,
            RiskScore = riskSnapshot.RiskScore,
            RiskReasons = riskSnapshot.RiskReasons,
            BlockedByRisk = riskSnapshot.BlockedByRisk,
            FalsePositive = riskSnapshot.FalsePositive,
            CountryCode = riskSnapshot.CountryCode,
            RiskOverrideId = riskSnapshot.RiskOverrideId
        };
    }
}

internal static class SecurityLogQueryExtensions
{
    public static IQueryable<IdentitySecurityLog> ApplySorting(this IQueryable<IdentitySecurityLog> query, string? sorting)
    {
        var normalizedSorting = sorting?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSorting))
        {
            return query.OrderByDescending(x => x.CreationTime);
        }

        return normalizedSorting.ToLowerInvariant() switch
        {
            // Bare and "desc" were mapped but explicit "asc" was not, so "creationtime asc" fell through
            // to the newest-first default — the caller's direction was inverted rather than refused.
            "creationtime" or "creationtime asc" => query.OrderBy(x => x.CreationTime).ThenBy(x => x.Id),
            "creationtime desc" => query.OrderByDescending(x => x.CreationTime).ThenByDescending(x => x.Id),
            "username" or "username asc" => query.OrderBy(x => x.UserName).ThenBy(x => x.Id),
            "username desc" => query.OrderByDescending(x => x.UserName).ThenByDescending(x => x.Id),
            "action" or "action asc" => query.OrderBy(x => x.Action).ThenBy(x => x.Id),
            "action desc" => query.OrderByDescending(x => x.Action).ThenByDescending(x => x.Id),
            "riskscore" or "riskscore asc" => query.OrderBy(x => x.RiskScore).ThenBy(x => x.Id),
            "riskscore desc" => query.OrderByDescending(x => x.RiskScore).ThenByDescending(x => x.Id),
            _ => query.OrderByDescending(x => x.CreationTime).ThenByDescending(x => x.Id)
        };
    }
}
