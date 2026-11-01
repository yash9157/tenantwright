using System;
using System.Threading.Tasks;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Contracts.Users;
using Shared;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Identity.Application.Users;

/// <summary>
/// Host-context facade for operations on a tenant-owned user. The explicit tenant
/// route segment is verified before the tenant-scoped user service is entered.
/// </summary>
internal sealed class HostTenantUserAppService : IHostTenantUserAppService, ITransientDependency
{
    private readonly IUserAppService _userAppService;
    private readonly TenantUserManagementGuard _managementGuard;
    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantStore _tenantStore;

    public HostTenantUserAppService(
        IUserAppService userAppService,
        TenantUserManagementGuard managementGuard,
        ICurrentTenant currentTenant,
        ITenantStore tenantStore)
    {
        _userAppService = userAppService;
        _managementGuard = managementGuard;
        _currentTenant = currentTenant;
        _tenantStore = tenantStore;
    }

    public async Task<UserDetailDto> GetAsync(Guid tenantId, string id)
    {
        var (tenant, detail) = await InTenantScopeAsync(tenantId, () => _userAppService.GetAsync(id));
        detail.TenantName = tenant.Name;
        return detail;
    }

    public Task<PagedResultDto<UserDto>> GetPagedListAsync(GetHostTenantUsersInput input)
    {
        _managementGuard.EnsureHostManagementActor();
        return _userAppService.GetHostPagedListAsync(input);
    }

    public async Task<UserDto> UpdateAsync(Guid tenantId, string id, UserUpdateDto input)
    {
        var (tenant, user) = await InTenantScopeAsync(tenantId, () => _userAppService.UpdateAsync(id, input));
        user.TenantName = tenant.Name;
        return user;
    }

    public async Task<UserDto> SetRolesAsync(Guid tenantId, string id, IdentityRoleAssignmentInput input)
    {
        var (tenant, user) = await InTenantScopeAsync(tenantId, () => _userAppService.SetRolesAsync(id, input));
        user.TenantName = tenant.Name;
        return user;
    }

    public async Task<UserDto> SuspendAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.SuspendAsync(id, input));

    public async Task<UserDto> RestoreAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.RestoreAsync(id, input));

    public async Task<UserDto> TerminateAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.TerminateAsync(id, input));

    public async Task<UserDto> RehireAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.RehireAsync(id, input));

    public async Task<UserDto> ArchiveAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.ArchiveAsync(id, input));

    public async Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        Guid tenantId,
        string id,
        GetIdentityLifecycleHistoryInput input)
    {
        var (_, history) = await InTenantScopeAsync(tenantId, () => _userAppService.GetLifecycleHistoryAsync(id, input));
        return history;
    }

    public async Task<UserDto> LockAsync(Guid tenantId, string id, IdentityLockStateChangeInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.LockAsync(id, input));

    public async Task<UserDto> UnlockAsync(Guid tenantId, string id, IdentityLockStateChangeInput input)
        => await ExecuteTransitionAsync(tenantId, () => _userAppService.UnlockAsync(id, input));

    public async Task<TenantPrimaryAdminDto> HandoverPrimaryAdministratorAsync(
        Guid tenantId,
        PrimaryAdministratorHandoverInput input)
    {
        var (_, primaryAdministrator) = await InTenantScopeAsync(
            tenantId,
            () => _userAppService.HandoverPrimaryAdministratorAsync(input));
        return primaryAdministrator;
    }

    private async Task<UserDto> ExecuteTransitionAsync(Guid tenantId, Func<Task<UserDto>> operation)
    {
        var (tenant, user) = await InTenantScopeAsync(tenantId, operation);
        user.TenantName = tenant.Name;
        return user;
    }

    private async Task<(TenantConfiguration Tenant, T Result)> InTenantScopeAsync<T>(
        Guid tenantId,
        Func<Task<T>> operation)
    {
        _managementGuard.EnsureHostManagementActor();
        if (tenantId == Guid.Empty)
        {
            throw new SharedValidationException("A tenant identifier is required.");
        }

        var tenant = await _tenantStore.FindAsync(tenantId)
            ?? throw new SharedValidationException("The target tenant does not exist or is unavailable.");
        using var tenantScope = _currentTenant.Change(tenant.Id, tenant.Name);
        return (tenant, await operation());
    }
}
