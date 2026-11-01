namespace Permissions.Domain.Shared.Constants;

/// <summary>
/// Stable caller-safe error codes for Permissions public catalog operations.
/// </summary>
public static class PermissionsErrorCodes
{
    public const string PermissionCatalogStaticDefinitionImmutable = "permission_catalog_static_definition_immutable";
    public const string PermissionCatalogNameExists = "permission_catalog_name_exists";
    public const string PermissionCatalogParentSelf = "permission_catalog_parent_self";
    public const string PermissionCatalogParentNotFound = "permission_catalog_parent_not_found";
    public const string PermissionCatalogHierarchyDepthExceeded = "permission_catalog_hierarchy_depth_exceeded";
    public const string PermissionCatalogHierarchyCycle = "permission_catalog_hierarchy_cycle";
    public const string UnsupportedFeatureProvider = "unsupported_feature_provider";
    public const string FeatureProvisioningRequired = "feature_provisioning_required";
    public const string FeatureDependencyNotSatisfied = "feature_dependency_not_satisfied";
}
