using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Tenant;
using Shared.Security;
using Tenant.Application.Contracts.Tenants;
using Tenant.Domain.Shared;
using Tenant.Domain.Shared.Enums;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

public sealed class TenantLaunchReadinessAppService(
    ICurrentTenant currentTenant,
    IDbContextProvider<TenantDbContextApp> dbContextProvider,
    IPermissionChecker permissionChecker,
    ICurrentPrincipalAccessor principalAccessor,
    TimeProvider timeProvider) : ITenantLaunchReadinessAppService, ITransientDependency
{
    public async Task<TenantLaunchReadinessDto> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!await permissionChecker.IsGrantedAsync(
                principalAccessor.Principal, TenantPermissions.Launch.Read, cancellationToken))
            throw new SharedForbiddenException("Tenant launch readiness permission is required.");
        var tenantId = currentTenant.Id ?? throw new SharedValidationException(TenantErrorCodes.TenantContextRequired,
            "An active tenant is required.");
        var checkedAtUtc = timeProvider.GetUtcNow();
        var db = await dbContextProvider.GetDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == tenantId,
            cancellationToken) ?? throw new KeyNotFoundException("Tenant was not found.");
        var ownership = await db.TenantOwnershipRequests.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.Kind == TenantOwnershipRequestKind.InitialOwner)
            .OrderByDescending(item => item.CreationTime)
            .FirstOrDefaultAsync(cancellationToken);
        var evidence = new List<TenantLaunchEvidenceDto>
        {
            new("invitation-created", "Identity", ownership?.InvitationCreatedAtUtc is not null,
                null, ownership?.InvitationCreatedAtUtc,
                ownership?.InvitationCreatedAtUtc is not null ? "Tenant administrator invitation was created." :
                    "Invite the tenant administrator.", "/workforce/invitations"),
            new("membership-active", "Identity", ownership?.CompletedAtUtc is not null && tenant.OwnerUserId is not null,
                tenant.LifecycleRevision, ownership?.CompletedAtUtc,
                ownership?.CompletedAtUtc is not null && tenant.OwnerUserId is not null ?
                    "Tenant administrator membership is active." : "The invited administrator must accept and verify membership.",
                "/workforce/users"),
            new("permissions-provisioned", "Permissions", ownership?.PermissionsProvisionedAtUtc is not null,
                null, ownership?.PermissionsProvisionedAtUtc,
                ownership?.PermissionsProvisionedAtUtc is not null ? "Initial administrator permissions are provisioned." :
                    "Select and provision the tenant administrator permission bundle.", "/access/delegation"),
            new("tenant-active", "Tenant", tenant.LifecycleStatus == TenantLifecycleStatus.Active && tenant.IsActive,
                tenant.LifecycleRevision, tenant.LastModificationTime ?? tenant.CreationTime,
                tenant.LifecycleStatus == TenantLifecycleStatus.Active && tenant.IsActive ? "Tenant is active." :
                    "Complete tenant activation.", "/")
        };
        var blockers = evidence.Where(item => !item.IsReady).Select(item => item.Code).ToArray();
        var state = blockers.Length == 0 ? TenantLaunchReadinessState.Ready : TenantLaunchReadinessState.Blocked;
        return new(tenantId, state, checkedAtUtc, evidence, blockers);
    }
}
