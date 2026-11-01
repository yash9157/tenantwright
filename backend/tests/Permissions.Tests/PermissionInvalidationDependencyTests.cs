using Permissions.Application.Caching;
using Permissions.Application.PermissionManagement;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionInvalidationDependencyTests
{
    [Fact]
    public void Permission_import_export_should_require_invalidation_dependencies()
    {
        var constructor = Assert.Single(typeof(PermissionImportExportAppService).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Contains(parameters, x =>
            x.ParameterType == typeof(PermissionProviderGrantCacheSyncService) &&
            !x.IsOptional);
        Assert.Contains(parameters, x =>
            x.ParameterType == typeof(PermissionStampService) &&
            !x.IsOptional);
    }

    [Fact]
    public void Resource_permission_management_should_require_stamp_service()
    {
        var constructor = Assert.Single(typeof(ResourcePermissionManagementAppService).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Contains(parameters, x =>
            x.ParameterType == typeof(PermissionStampService) &&
            !x.IsOptional);
    }
}
