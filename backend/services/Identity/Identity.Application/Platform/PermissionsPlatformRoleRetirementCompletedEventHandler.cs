using Identity.Application.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Platform;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<PermissionsPlatformRoleRetirementCompletedEto>),
    IncludeSelf = true)]
public sealed class PermissionsPlatformRoleRetirementCompletedEventHandler
    : ITenantWrightMessageHandler<PermissionsPlatformRoleRetirementCompletedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ApplicationRoleManager _roleManager;

    public PermissionsPlatformRoleRetirementCompletedEventHandler(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider,
        ApplicationRoleManager roleManager)
    {
        _dbContextProvider = dbContextProvider;
        _roleManager = roleManager;
    }

    public async Task HandleAsync(
        PermissionsPlatformRoleRetirementCompletedEto eventData,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var role = await unitOfWorkDbContext.Roles.SingleOrDefaultAsync(
            x => x.Id == eventData.RoleId && x.TenantId == null,
            cancellationToken);
        if (role == null)
        {
            return;
        }

        if (role.LifecycleState != ApplicationRoleLifecycleState.Retiring ||
            role.LifecycleVersion != eventData.LifecycleVersion ||
            role.RetirementRequestId != eventData.RequestId)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Platform role retirement could not be finalized: {string.Join(", ", result.Errors.Select(x => x.Code))}");
        }
    }
}
