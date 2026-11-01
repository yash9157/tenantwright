using Identity.Application.ApplicationUsers;
using Identity.Application.Caching;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Users;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Identity.Application.Users;

public class InternalUserProvisioningAppService : IInternalUserProvisioningAppService, ITransientDependency
{
    private readonly ApplicationUserManager _userManager;
    private readonly ApplicationRoleManager _roleManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;

    public InternalUserProvisioningAppService(
        ApplicationUserManager userManager,
        ApplicationRoleManager roleManager,
        ICurrentTenant currentTenant,
        IIdentityAuthCacheInvalidator cacheInvalidator)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _currentTenant = currentTenant;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<ProvisionFirstUserResultDto> ProvisionFirstUserAsync(
        ProvisionFirstUserInput input,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _currentTenant.RequireTenantId();
        var roleName = NormalizeRoleName(input.RoleName);

        var tenantHasUsers = await _userManager.Users
            .AnyAsync(x => x.TenantId == tenantId, cancellationToken);
        if (tenantHasUsers)
        {
            return new ProvisionFirstUserResultDto
            {
                Succeeded = false,
                TenantAlreadyInitialized = true,
                Error = "Tenant already has users."
            };
        }

        await EnsureRoleAsync(tenantId, roleName, cancellationToken);

        var user = new ApplicationUser
        {
            UserName = input.UserName.Trim(),
            Email = input.Email.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(input.PhoneNumber) ? null : input.PhoneNumber.Trim(),
            IsActive = input.IsActive,
            RequireEmailActivationBeforeLogin = input.RequireEmailActivationBeforeLogin,
            TenantId = tenantId
        };
        user.InitializeLifecycle(input.IsActive);

        var createResult = await _userManager.CreateAsync(user, input.Password);
        if (!createResult.Succeeded)
        {
            return new ProvisionFirstUserResultDto
            {
                Succeeded = false,
                Error = string.Join(", ", createResult.Errors.Select(x => x.Description))
            };
        }

        var addRoleResult = await _userManager.AddToRoleAsync(user, roleName);
        if (!addRoleResult.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            return new ProvisionFirstUserResultDto
            {
                Succeeded = false,
                Error = string.Join(", ", addRoleResult.Errors.Select(x => x.Description))
            };
        }

        await _cacheInvalidator.InvalidateProfileAsync(user.TenantId, user.Id, cancellationToken);
        await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id, cancellationToken);
        await _cacheInvalidator.InvalidateAliasesAsync(
            user.TenantId,
            IdentityUserCacheContracts.BuildAliasHashes(user.UserName, user.Email, user.PhoneNumber),
            cancellationToken);

        return new ProvisionFirstUserResultDto
        {
            Succeeded = true,
            UserId = user.Id.ToString()
        };
    }

    private async Task EnsureRoleAsync(Guid tenantId, string roleName, CancellationToken cancellationToken)
    {
        var normalizedRoleName = roleName.Trim().ToUpperInvariant();
        var isTenantAdmin = TenantWrightIdentityRoleNames.IsTenantAdminRole(roleName);
        var canonicalRoleId = isTenantAdmin ? IdentitySystemRoleIds.TenantAdmin(tenantId) : (Guid?)null;

        var existing = await _roleManager.Roles
            .FirstOrDefaultAsync(
                x => x.TenantId == tenantId &&
                     (canonicalRoleId.HasValue ? x.Id == canonicalRoleId.Value : x.NormalizedName == normalizedRoleName),
                cancellationToken);
        if (existing != null)
        {
            if (isTenantAdmin && !string.Equals(existing.NormalizedName, normalizedRoleName, StringComparison.Ordinal))
            {
                throw new SharedValidationException("The canonical TenantAdmin role has an unexpected name.");
            }

            return;
        }

        if (isTenantAdmin)
        {
            var namedRole = await _roleManager.Roles
                .FirstOrDefaultAsync(
                    x => x.TenantId == tenantId && x.NormalizedName == normalizedRoleName,
                    cancellationToken);
            if (namedRole != null)
            {
                throw new SharedValidationException(
                    "The TenantAdmin role does not use its canonical immutable identifier. Reset the pre-launch Identity database and reseed it.");
            }
        }

        var role = new ApplicationRole(roleName)
        {
            TenantId = tenantId,
            Id = canonicalRoleId ?? Guid.CreateVersion7()
        };

        var roleResult = await _roleManager.CreateAsync(role);
        if (roleResult.Succeeded)
        {
            return;
        }

        existing = await _roleManager.Roles
            .FirstOrDefaultAsync(
                x => x.TenantId == tenantId &&
                     (canonicalRoleId.HasValue ? x.Id == canonicalRoleId.Value : x.NormalizedName == normalizedRoleName),
                cancellationToken);
        if (existing != null)
        {
            if (isTenantAdmin && !string.Equals(existing.NormalizedName, normalizedRoleName, StringComparison.Ordinal))
            {
                throw new SharedValidationException("The canonical TenantAdmin role has an unexpected name.");
            }

            return;
        }

        throw new SharedValidationException(string.Join(", ", roleResult.Errors.Select(x => x.Description)));
    }

    private static string NormalizeRoleName(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return TenantWrightIdentityRoleNames.TenantAdmin;
        }

        return TenantWrightIdentityRoleNames.Normalize(roleName);
    }
}
