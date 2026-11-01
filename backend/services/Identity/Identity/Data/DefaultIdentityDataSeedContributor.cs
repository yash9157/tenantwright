using System;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.SecuritySettings;
using Identity.Application.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;

namespace Identity.Data;

public class DefaultIdentityDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IOptions<IdentityBootstrapSeedOptions> _bootstrapSeedOptions;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly HostIdentitySecurityStaticOptions _hostIdentitySecurityStaticOptions;
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;

    public DefaultIdentityDataSeedContributor(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        ICurrentTenant currentTenant,
        IOptions<IdentityBootstrapSeedOptions> bootstrapSeedOptions,
        IDbContextProvider<IdentityDbContextApp> dbContextProvider,
        HostIdentitySecurityStaticOptions hostIdentitySecurityStaticOptions,
        HostIdentitySecurityPolicyRuntimeState runtimeState)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _currentTenant = currentTenant;
        _bootstrapSeedOptions = bootstrapSeedOptions;
        _dbContextProvider = dbContextProvider;
        _hostIdentitySecurityStaticOptions = hostIdentitySecurityStaticOptions;
        _runtimeState = runtimeState;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        using var _ = _currentTenant.Change(id: null, name: null);
        await EnsureHostSecurityPolicyAsync();

        var options = _bootstrapSeedOptions.Value;
        if (!options.Enabled)
        {
            return;
        }

        if (options.HostAdmin.Enabled)
        {
            await SeedHostAdminAsync(options.HostAdmin);
        }

        if (options.TenantAdmin.Enabled)
        {
            await SeedTenantAdminAsync(options.TenantAdmin);
        }
    }

    private async Task SeedHostAdminAsync(IdentityBootstrapHostAdminOptions options)
    {
        var tenantId = options.TenantId;
        var userName = options.UserName!.Trim();
        var email = options.Email!.Trim();
        var password = options.Password!;

        await EnsureRoleExistsAsync("admin", IdentitySystemRoleIds.HostAdmin, tenantId: null, tenantName: null);

        var user = await _userManager.Users.FirstOrDefaultAsync(x =>
            x.UserName == userName && x.TenantId == tenantId);
        if (user == null)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserName = userName,
                Email = email,
                EmailConfirmed = true,
                IsActive = true
            };
            user.InitializeLifecycle(isActive: true);
            var userResult = await _userManager.CreateAsync(user, password);
            if (!userResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to create default admin user: " + string.Join("; ", userResult.Errors.Select(x => x.Description)));
            }
        }
        else if (options.ResetPasswordOnSeed)
        {
            await ResetPasswordForSeededUserAsync(user, password);
        }

        if (!await _userManager.IsInRoleAsync(user, "admin"))
        {
            var addRoleResult = await _userManager.AddToRoleAsync(user, "admin");
            if (!addRoleResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to assign admin role to default user: " + string.Join("; ", addRoleResult.Errors.Select(x => x.Description)));
            }
        }
    }

    private async Task SeedTenantAdminAsync(IdentityBootstrapTenantAdminOptions options)
    {
        var tenantId = options.TenantId;
        var tenantName = options.TenantName!.Trim();
        var userName = options.UserName!.Trim();
        var email = options.Email!.Trim();
        var password = options.Password!;

        await EnsureRoleExistsAsync(
            TenantWrightIdentityRoleNames.TenantAdmin,
            IdentitySystemRoleIds.TenantAdmin(tenantId),
            tenantId,
            tenantName);

        using (_currentTenant.Change(tenantId, tenantName))
        {
            var user = await _userManager.Users.FirstOrDefaultAsync(x =>
                x.UserName == userName && x.TenantId == tenantId);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    UserName = userName,
                    Email = email,
                    EmailConfirmed = true,
                    IsActive = true
                };
                user.InitializeLifecycle(isActive: true);
                var userResult = await _userManager.CreateAsync(user, password);
                if (!userResult.Succeeded)
                {
                    throw new InvalidOperationException("Failed to create default tenant admin user: " + string.Join("; ", userResult.Errors.Select(x => x.Description)));
                }
            }
            else
            {
                var changed = false;
                if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
                {
                    user.Email = email;
                    changed = true;
                }

                if (!user.EmailConfirmed)
                {
                    user.EmailConfirmed = true;
                    changed = true;
                }

                if (changed)
                {
                    var updateResult = await _userManager.UpdateAsync(user);
                    if (!updateResult.Succeeded)
                    {
                        throw new InvalidOperationException("Failed to update default tenant admin user: " + string.Join("; ", updateResult.Errors.Select(x => x.Description)));
                    }
                }

                if (options.ResetPasswordOnSeed)
                {
                    await ResetPasswordForSeededUserAsync(user, password);
                }
            }

            if (!await _userManager.IsInRoleAsync(user, TenantWrightIdentityRoleNames.TenantAdmin))
            {
                var addRoleResult = await _userManager.AddToRoleAsync(user, TenantWrightIdentityRoleNames.TenantAdmin);
                if (!addRoleResult.Succeeded)
                {
                    throw new InvalidOperationException("Failed to assign tenant admin role to default tenant user: " + string.Join("; ", addRoleResult.Errors.Select(x => x.Description)));
                }
            }
        }
    }

    private async Task ResetPasswordForSeededUserAsync(ApplicationUser user, string password)
    {
        if (!string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            var removeResult = await _userManager.RemovePasswordAsync(user);
            if (!removeResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to reset default tenant admin password: " + string.Join("; ", removeResult.Errors.Select(x => x.Description)));
            }
        }

        var addResult = await _userManager.AddPasswordAsync(user, password);
        if (!addResult.Succeeded)
        {
            throw new InvalidOperationException("Failed to set default tenant admin password: " + string.Join("; ", addResult.Errors.Select(x => x.Description)));
        }
    }

    private async Task EnsureHostSecurityPolicyAsync()
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var entity = await dbContext.HostIdentitySecurityPolicies
            .FirstOrDefaultAsync(x => x.Id == HostIdentitySecurityPolicy.SingletonId);
        if (entity == null)
        {
            entity = HostIdentitySecuritySettingsMapper.CreatePolicy(_hostIdentitySecurityStaticOptions.DefaultSettings);
            await dbContext.HostIdentitySecurityPolicies.AddAsync(entity);
            await dbContext.SaveChangesAsync();
        }

        _runtimeState.SetCurrent(HostIdentitySecuritySettingsMapper.FromPolicy(entity));
    }

    private async Task EnsureRoleExistsAsync(
        string roleName,
        Guid roleId,
        Guid? tenantId,
        string? tenantName)
    {
        var normalizedRoleName = roleName.ToUpperInvariant();

        using (_currentTenant.Change(tenantId, tenantName))
        {
            var existingRole = await _roleManager.Roles.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.NormalizedName == normalizedRoleName);
            if (existingRole != null)
            {
                if (existingRole.Id != roleId)
                {
                    throw new InvalidOperationException(
                        $"System role '{roleName}' does not use its canonical immutable identifier. Reset the pre-launch Identity database and reseed it.");
                }

                return;
            }

            var role = new ApplicationRole(roleName)
            {
                Id = roleId,
                TenantId = tenantId
            };

            var roleResult = await _roleManager.CreateAsync(role);
            if (roleResult.Succeeded)
            {
                return;
            }

            existingRole = await _roleManager.Roles.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.NormalizedName == normalizedRoleName);
            if (existingRole != null)
            {
                if (existingRole.Id != roleId)
                {
                    throw new InvalidOperationException(
                        $"System role '{roleName}' does not use its canonical immutable identifier. Reset the pre-launch Identity database and reseed it.");
                }

                return;
            }

            throw new InvalidOperationException($"Failed to create role '{roleName}': " + string.Join("; ", roleResult.Errors.Select(x => x.Description)));
        }
    }
}
