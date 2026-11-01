using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.Contracts.ClaimTypes;
using Identity.Application.Contracts.Users;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.ClaimTypes;

public class IdentityClaimTypeAppService : IIdentityClaimTypeAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;

    public IdentityClaimTypeAppService(IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
    }

    public async Task<ClaimTypeDto> GetAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var claimType = await unitOfWorkDbContext.IdentityClaimTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        if (claimType == null) throw new EntityNotFoundException<IdentityClaimType>(id);

        return MapToDto(claimType);
    }

    public async Task<PagedResultDto<ClaimTypeDto>> GetListAsync(ClaimTypeListInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();

        var query = unitOfWorkDbContext.IdentityClaimTypes
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        var filter = input.Filter?.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            var pattern = $"%{filter}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.Name, pattern) ||
                (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        var totalCount = await query.LongCountAsync();
        var list = await ApplySorting(query, SortingValidator.ValidateAndNormalize<ClaimTypeListInput>(input.Sorting))
            .Skip(Math.Max(0, input.SkipCount))
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToListAsync();

        return new PagedResultDto<ClaimTypeDto>
        {
            TotalCount = totalCount,
            Items = list.Select(MapToDto).ToList()
        };
    }

    /// <summary>
    /// Maps a validated sort to an ordering. <c>SortableFieldsAttribute</c> on
    /// <see cref="ClaimTypeListInput"/> is the whitelist, so this mapping must stay complete for every
    /// declared field — a declared-but-unmapped field would fall through to the default and answer a
    /// different question than the caller asked.
    /// </summary>
    private static IQueryable<IdentityClaimType> ApplySorting(
        IQueryable<IdentityClaimType> query,
        string? sorting)
    {
        // Each branch ends with Id so offset paging is deterministic; without a total order a page can
        // repeat or drop rows. A bare field name means ascending.
        return sorting?.Trim().ToLowerInvariant() switch
        {
            "valuetype" or "valuetype asc" =>
                query.OrderBy(x => x.ValueType).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "valuetype desc" =>
                query.OrderByDescending(x => x.ValueType).ThenBy(x => x.Name).ThenBy(x => x.Id),
            // Required first is the useful direction when auditing which claims are mandatory, but both
            // are offered because the whitelist declares the field, not a direction.
            "required" or "required asc" =>
                query.OrderBy(x => x.Required).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "required desc" =>
                query.OrderByDescending(x => x.Required).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "name desc" => query.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            _ => query.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
    }


    [Audited]
    public async Task<ClaimTypeDto> CreateAsync(ClaimTypeCreateDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();

        var claimType = new IdentityClaimType
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = input.Name,
            Required = input.Required,
            Regex = input.Regex,
            RegexDescription = input.RegexDescription,
            Description = input.Description,
            ValueType = Enum.Parse<IdentityClaimValueType>(input.ValueType)
        };

        await unitOfWorkDbContext.AddAsync(claimType);
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapToDto(claimType);
    }


    [Audited]
    public async Task<ClaimTypeDto> UpdateAsync(Guid id, ClaimTypeUpdateDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var claimType = await unitOfWorkDbContext.IdentityClaimTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (claimType == null) throw new EntityNotFoundException<IdentityClaimType>(id);

        claimType.Required = input.Required;
        claimType.Regex = input.Regex;
        claimType.RegexDescription = input.RegexDescription;
        claimType.Description = input.Description;

        await unitOfWorkDbContext.SaveChangesAsync();

        return MapToDto(claimType);
    }


    [Audited]
    public async Task DeleteAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var claimType = await unitOfWorkDbContext.IdentityClaimTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (claimType == null || claimType.IsStatic) return;

        unitOfWorkDbContext.Remove(claimType);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    public Task<ClaimTypeDto> GetPlatformAsync(Guid tenantId, Guid id)
        => ExecuteInPlatformTenantScopeAsync(tenantId, () => GetAsync(id));

    public Task<PagedResultDto<ClaimTypeDto>> GetPlatformListAsync(Guid tenantId, ClaimTypeListInput input)
        => ExecuteInPlatformTenantScopeAsync(tenantId, () => GetListAsync(input));

    [Audited]
    public Task<ClaimTypeDto> CreatePlatformAsync(Guid tenantId, ClaimTypeCreateDto input)
        => ExecuteInPlatformTenantScopeAsync(tenantId, () => CreateAsync(input));

    [Audited]
    public Task<ClaimTypeDto> UpdatePlatformAsync(Guid tenantId, Guid id, ClaimTypeUpdateDto input)
        => ExecuteInPlatformTenantScopeAsync(tenantId, () => UpdateAsync(id, input));

    [Audited]
    public Task DeletePlatformAsync(Guid tenantId, Guid id)
        => ExecuteInPlatformTenantScopeAsync(tenantId, () => DeleteAsync(id));

    private async Task<T> ExecuteInPlatformTenantScopeAsync<T>(Guid tenantId, Func<Task<T>> action)
    {
        EnsurePlatformTenantScope(tenantId);
        using (_currentTenant.Change(tenantId))
        {
            return await action();
        }
    }

    private async Task ExecuteInPlatformTenantScopeAsync(Guid tenantId, Func<Task> action)
    {
        EnsurePlatformTenantScope(tenantId);
        using (_currentTenant.Change(tenantId))
        {
            await action();
        }
    }

    private void EnsurePlatformTenantScope(Guid tenantId)
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Platform claim-type management requires host scope.");
        }

        if (tenantId == Guid.Empty)
        {
            throw new SharedValidationException("A target tenant is required.");
        }
    }

    private static ClaimTypeDto MapToDto(IdentityClaimType claimType)
    {
        return new ClaimTypeDto
        {
            Id = claimType.Id,
            Name = claimType.Name,
            Required = claimType.Required,
            IsStatic = claimType.IsStatic,
            Regex = claimType.Regex,
            RegexDescription = claimType.RegexDescription,
            Description = claimType.Description,
            ValueType = claimType.ValueType.ToString()
        };
    }
}
