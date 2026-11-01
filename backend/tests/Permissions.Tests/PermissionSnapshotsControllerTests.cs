using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Controllers.PermissionSnapshots;
using Shared.Security;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionSnapshotsControllerTests
{
    [Fact]
    public async Task Public_snapshot_endpoint_should_use_current_principal_subject()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var firstRoleId = Guid.NewGuid();
        var secondRoleId = Guid.NewGuid();
        var service = new RecordingSnapshotService();
        var controller = new PermissionSnapshotsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(SharedClaimTypes.Subject, userId.ToString("D")),
                            new Claim(SharedClaimTypes.TenantId, tenantId.ToString("D")),
                            new Claim(SharedClaimTypes.IdentityRoleId, firstRoleId.ToString("D")),
                            new Claim(SharedClaimTypes.IdentityRoleId, secondRoleId.ToString("D"))
                        ],
                        "test"))
                }
            }
        };

        await controller.GetAsync(CancellationToken.None);

        Assert.Equal((tenantId, userId), service.LastPublicSubject);
        Assert.Equal(new[] { firstRoleId, secondRoleId }.OrderBy(x => x), service.LastPublicRoleIds);
    }

    [Fact]
    public void Snapshot_routes_should_split_public_current_subject_from_internal_arbitrary_subject()
    {
        var publicAuthorize = typeof(PermissionSnapshotsController)
            .GetMethod(nameof(PermissionSnapshotsController.GetAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        var internalAuthorize = typeof(InternalPermissionSnapshotsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        var publicParameters = typeof(PermissionSnapshotsController)
            .GetMethod(nameof(PermissionSnapshotsController.GetAsync))!
            .GetParameters();

        Assert.Equal(PermissionsPolicies.ReadSnapshots, publicAuthorize.Policy);
        Assert.Equal(PermissionsPolicies.CheckFeaturesInternal, internalAuthorize.Policy);
        Assert.DoesNotContain(publicParameters, x => x.Name is "tenantId" or "userId" or "roleNames");
        Assert.Equal("api/permissions/internal/permission-snapshots", typeof(InternalPermissionSnapshotsController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single()
            .Template);
    }

    private sealed class RecordingSnapshotService : IPermissionSnapshotAppService
    {
        public (Guid? TenantId, Guid UserId)? LastPublicSubject { get; private set; }
        public List<Guid> LastPublicRoleIds { get; private set; } = [];

        public Task<PermissionSnapshotDto> GetSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default)
        {
            LastPublicSubject = (tenantId, userId);
            LastPublicRoleIds = roleIds;
            return Task.FromResult(new PermissionSnapshotDto());
        }

        public Task<PermissionSnapshotDto> GetInternalSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
