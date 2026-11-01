using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.Contracts.Users;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public class IdentityUserDelegationAppService : IIdentityUserDelegationAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;

    public IdentityUserDelegationAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentUser currentUser, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentUser = currentUser;
        _currentTenant = currentTenant;
    }

    public async Task<List<IdentityUserDelegationDto>> GetListAsync()
    {
        var sourceUserId = RequireCurrentUserId();
        return await GetListForSourceAsync(sourceUserId);
    }

    public async Task<List<IdentityUserDelegationDto>> GetListForSourceAsync(Guid sourceUserId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (sourceUserId == Guid.Empty)
        {
            throw new SharedValidationException("Source user is required.");
        }

        var tenantId = _currentTenant.RequireTenantId();
        await RequireActiveTenantUserAsync(sourceUserId, tenantId, "Source user does not exist in the current tenant.");

        var delegations = await unitOfWorkDbContext.IdentityUserDelegations
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceUserId == sourceUserId)
            .OrderByDescending(x => x.StartTime)
            .ToListAsync();

        return await MapDelegationsAsync(delegations);
    }

    public async Task<List<IdentityUserDelegationDto>> GetActiveDelegationsAsync()
    {
        var targetUserId = RequireCurrentUserId();
        return await GetActiveDelegationsForTargetAsync(targetUserId);
    }

    public async Task<List<IdentityUserDelegationDto>> GetActiveDelegationsForTargetAsync(Guid targetUserId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (targetUserId == Guid.Empty)
        {
            throw new SharedValidationException("Target user is required.");
        }

        var tenantId = _currentTenant.RequireTenantId();
        await RequireActiveTenantUserAsync(targetUserId, tenantId, "Target user does not exist in the current tenant.");
        var now = DateTime.UtcNow;

        var delegations = await unitOfWorkDbContext.IdentityUserDelegations
            .AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.TargetUserId == targetUserId &&
                x.StartTime <= now &&
                x.EndTime >= now)
            .OrderByDescending(x => x.StartTime)
            .ToListAsync();

        return await MapDelegationsAsync(delegations);
    }

    [Audited]
    public async Task DelegateNewUserAsync(CreateUserDelegationDto input)
    {
        var sourceUserId = RequireCurrentUserId();
        await DelegateNewUserForSourceAsync(new CreateUserDelegationForSourceDto
        {
            SourceUserId = sourceUserId,
            TargetUserId = input.TargetUserId,
            StartTime = input.StartTime,
            EndTime = input.EndTime
        });
    }

    [Audited]
    public async Task DelegateNewUserForSourceAsync(CreateUserDelegationForSourceDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var sourceUserId = input.SourceUserId;

        if (sourceUserId == Guid.Empty)
        {
            throw new SharedValidationException("Source user is required.");
        }

        if (input.TargetUserId == Guid.Empty)
        {
            throw new SharedValidationException("Target user is required.");
        }

        if (sourceUserId == input.TargetUserId)
        {
            throw new SharedValidationException("You cannot delegate to yourself.");
        }

        var startUtc = NormalizeUtc(input.StartTime);
        var endUtc = NormalizeUtc(input.EndTime);
        if (startUtc >= endUtc)
        {
            throw new SharedValidationException("Delegation start time must be earlier than end time.");
        }

        await RequireActiveTenantUserAsync(sourceUserId, tenantId, "Source user does not exist in the current tenant.");

        var targetUser = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == input.TargetUserId && x.TenantId == tenantId);
        if (targetUser == null)
        {
            throw new SharedValidationException("Target user does not exist in the current tenant.");
        }

        if (targetUser.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            throw new SharedValidationException("Target user must be active.");
        }

        var hasOverlappingDelegation = await unitOfWorkDbContext.IdentityUserDelegations.AnyAsync(x =>
            x.TenantId == tenantId &&
            x.SourceUserId == sourceUserId &&
            x.TargetUserId == input.TargetUserId &&
            x.EndTime >= startUtc &&
            x.StartTime <= endUtc);

        if (hasOverlappingDelegation)
        {
            throw new SharedValidationException("An active delegation for the selected user already exists in the requested window.");
        }

        await unitOfWorkDbContext.AddAsync(new IdentityUserDelegation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SourceUserId = sourceUserId,
            TargetUserId = input.TargetUserId,
            StartTime = startUtc,
            EndTime = endUtc
        });

        await unitOfWorkDbContext.SaveChangesAsync();
    }

    [Audited]
    public async Task DeleteDelegationAsync(Guid id)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        var sourceUserId = RequireCurrentUserId();
        await DeleteDelegationForSourceAsync(id, sourceUserId);
    }

    [Audited]
    public async Task DeleteDelegationForSourceAsync(Guid id, Guid sourceUserId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (id == Guid.Empty)
        {
            return;
        }

        if (sourceUserId == Guid.Empty)
        {
            throw new SharedValidationException("Source user is required.");
        }

        var tenantId = _currentTenant.RequireTenantId();
        await RequireActiveTenantUserAsync(sourceUserId, tenantId, "Source user does not exist in the current tenant.");

        var delegation = await unitOfWorkDbContext.IdentityUserDelegations.FirstOrDefaultAsync(x =>
            x.Id == id &&
            x.TenantId == tenantId &&
            x.SourceUserId == sourceUserId);

        if (delegation == null)
        {
            return;
        }

        unitOfWorkDbContext.Remove(delegation);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private Guid RequireCurrentUserId()
    {
        return _currentUser.Id ?? throw new SharedUnauthorizedException("User is not authenticated.");
    }

    private async Task RequireActiveTenantUserAsync(Guid userId, Guid tenantId, string notFoundMessage)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId);
        if (user == null)
        {
            throw new SharedValidationException(notFoundMessage);
        }

        if (user.GetEffectiveLifecycleStatus() != IdentityUserLifecycleStatus.Active)
        {
            throw new SharedValidationException("Selected user must be active.");
        }
    }

    private async Task<List<IdentityUserDelegationDto>> MapDelegationsAsync(List<IdentityUserDelegation> delegations)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (delegations.Count == 0)
        {
            return [];
        }

        var userIds = delegations
            .SelectMany(x => new[] { x.SourceUserId, x.TargetUserId })
            .Distinct()
            .ToList();

        var users = await unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new UserLookup
            {
                Id = x.Id,
                UserName = x.UserName,
                Email = x.Email
            })
            .ToDictionaryAsync(x => x.Id, x => x);

        return delegations.Select(x => MapToDto(x, users)).ToList();
    }

    private static IdentityUserDelegationDto MapToDto(
        IdentityUserDelegation delegation,
        IReadOnlyDictionary<Guid, UserLookup> users)
    {
        var now = DateTime.UtcNow;
        users.TryGetValue(delegation.SourceUserId, out var sourceUser);
        users.TryGetValue(delegation.TargetUserId, out var targetUser);

        return new IdentityUserDelegationDto
        {
            Id = delegation.Id,
            SourceUserId = delegation.SourceUserId,
            SourceUserName = sourceUser?.UserName,
            SourceEmail = sourceUser?.Email,
            TargetUserId = delegation.TargetUserId,
            TargetUserName = targetUser?.UserName,
            TargetEmail = targetUser?.Email,
            StartTime = delegation.StartTime,
            EndTime = delegation.EndTime,
            IsActive = delegation.StartTime <= now && delegation.EndTime >= now,
            IsExpired = delegation.EndTime < now
        };
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            throw new SharedValidationException("Delegation start and end times are required.");
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed class UserLookup
    {
        public Guid Id { get; init; }
        public string? UserName { get; init; }
        public string? Email { get; init; }
    }
}
