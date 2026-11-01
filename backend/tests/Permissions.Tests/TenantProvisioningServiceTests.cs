using Permissions.Application.Contracts.FeatureManagement;
using Identity.Domain.Shared.ApplicationUsers;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Definitions;
using Permissions.Application.Provisioning;
using Permissions.Application.PermissionManagement;
using Shared.MultiTenancy;
using Xunit;

namespace Permissions.Tests;

public sealed class TenantProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionTenantAsync_should_propagate_grant_provisioning_failure()
    {
        var expected = new InvalidOperationException("Grant provisioning failed.");
        var permissionService = new ThrowingPermissionManagementService(expected);
        var tenantId = Guid.NewGuid();
        var service = new TenantProvisioningService(
            permissionService,
            new RecordingFeatureManagementService(),
            new PermissionDefinitionManager([]),
            new TestCurrentTenant(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantProvisioningService>.Instance);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ProvisionTenantAsync(tenantId, []));

        Assert.Same(expected, actual);
        Assert.Equal(IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D"), permissionService.ProviderKey);
    }

    private sealed class ThrowingPermissionManagementService : IPermissionManagementAppService
    {
        private readonly Exception _exception;

        public ThrowingPermissionManagementService(Exception exception)
        {
            _exception = exception;
        }

        public string? ProviderKey { get; private set; }

        public Task<GetPermissionListResultDto> GetAsync(
            string providerName,
            string providerKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GetPermissionListResultDto
            {
                ProviderVersion = PermissionAssignmentAppService.InitialProviderVersion
            });

        public Task<GetPermissionListResultDto> GetByGroupAsync(
            string groupName,
            string providerName,
            string providerKey,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateAsync(
            string providerName,
            string providerKey,
            UpdatePermissionsDto input,
            CancellationToken cancellationToken = default)
        {
            ProviderKey = providerKey;
            return Task.FromException(_exception);
        }
    }

    private sealed class RecordingFeatureManagementService : IFeatureManagementAppService
    {
        public Task<GetFeatureListResultDto> GetAsync(
            string providerName,
            string providerKey,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateAsync(
            string providerName,
            string providerKey,
            UpdateFeaturesDto input,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BatchFeatureCheckResponseDto> BatchCheckAsync(
            BatchFeatureCheckRequestDto input,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            Id = id;
            return new DisposeAction(() => Id = previousId);
        }
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _action;

        public DisposeAction(Action action)
        {
            _action = action;
        }

        public void Dispose() => _action();
    }
}
