using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Identity.Application.Users;

/// <summary>
/// Purpose-limited directory search behind <c>Identity.Users.Search</c>.
///
/// Deliberately does not call <c>TenantUserManagementGuard.EnsureUserManagementActor()</c>:
/// that guard requires host- or tenant-admin context, and the callers this exists for —
/// credentialing officers picking a reviewer — are neither. Tenant scoping comes from the
/// ambient tenant instead, so a caller can only ever search inside their own tenant.
/// </summary>
internal sealed class UserDirectoryAppService(
    IDbContextProvider<IdentityDbContextApp> dbContextProvider,
    ICurrentTenant currentTenant) : IUserDirectoryAppService, ITransientDependency
{
    public async Task<IReadOnlyList<UserDirectoryEntryDto>> SearchAsync(
        SearchUserDirectoryInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var query = input.Query?.Trim() ?? string.Empty;

        // Enforced here as well as by model validation: a short or empty query returns nothing
        // rather than the directory, so this cannot degrade into an enumeration surface.
        if (query.Length < UserDirectoryConsts.MinQueryLength)
        {
            return [];
        }

        // Fail closed rather than falling back to host scope when no tenant is resolved.
        var tenantId = currentTenant.Id;
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
        {
            return [];
        }

        var take = Math.Clamp(
            input.MaxResultCount <= 0 ? UserDirectoryConsts.DefaultResultCount : input.MaxResultCount,
            1,
            UserDirectoryConsts.MaxResultCount);

        var dbContext = await dbContextProvider.GetDbContextAsync();

        return await dbContext.Users
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId.Value)
            // Only users who can actually act. A suspended or archived colleague is not a
            // candidate reviewer, and excluding them also keeps former staff out of the results.
            .Where(x => x.IsActive && x.LifecycleStatus == IdentityUserLifecycleStatus.Active)
            .Where(x =>
                x.UserName!.Contains(query) ||
                x.Email!.Contains(query) ||
                (x.Name != null && x.Name.Contains(query)) ||
                (x.Surname != null && x.Surname.Contains(query)))
            .OrderBy(x => x.Name ?? x.UserName)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new UserDirectoryEntryDto
            {
                Id = x.Id,
                DisplayName =
                    x.Name != null && x.Surname != null ? x.Name + " " + x.Surname :
                    x.Name ?? x.Surname ?? x.UserName!,
                Email = x.Email!,
            })
            .ToListAsync(cancellationToken);
    }
}
