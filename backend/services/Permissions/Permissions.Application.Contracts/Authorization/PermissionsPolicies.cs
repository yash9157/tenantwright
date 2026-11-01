namespace Permissions.Application.Contracts.Authorization;

public static class PermissionsPolicies
{
    public const string ManagePermissionGrants = "Permissions.Management.Grants";
    public const string ReadGrantApprovals = "Permissions.Management.Approve.Read";
    public const string DecideGrantApprovals = "Permissions.Management.Approve.Decide";
    public const string CancelAnyGrantApproval = "Permissions.Management.Approve.CancelAny";
    public const string ReadGrantHistory = "Permissions.Management.History";
    public const string ReadPermissionCatalog = "Permissions.Management.Catalog.Read";
    public const string CreatePermissionCatalog = "Permissions.Management.Catalog.Create";
    public const string UpdatePermissionCatalog = "Permissions.Management.Catalog.Update";
    public const string DeletePermissionCatalog = "Permissions.Management.Catalog.Delete";
    public const string ReadPermissionConflictRules = "Permissions.Management.ConflictRules.Read";
    public const string CreatePermissionConflictRules = "Permissions.Management.ConflictRules.Create";
    public const string UpdatePermissionConflictRules = "Permissions.Management.ConflictRules.Update";
    public const string DeletePermissionConflictRules = "Permissions.Management.ConflictRules.Delete";
    public const string DecidePermissionConflictRules = "Permissions.Management.ConflictRules.Decide";
    public const string ReadFeatures = "Permissions.Management.Features.Read";
    public const string UpdateFeatures = "Permissions.Management.Features.Update";
    public const string EvaluatePermissions = "Permissions.Management.Evaluate";
    public const string ManageAccessReviews = "Permissions.Management.Review";
    public const string ReadPermissionTransfers = "Permissions.Management.ImportExport.Read";
    public const string ExportPermissions = "Permissions.Management.ImportExport.Export";
    public const string ValidatePermissionImports = "Permissions.Management.ImportExport.Validate";
    public const string ApplyPermissionImports = "Permissions.Management.ImportExport.Apply";
    public const string ReadTenantAccess = "Permissions.TenantAccess.Read";
    public const string ManageTenantAccess = "Permissions.TenantAccess.Manage";
    public const string ReadSnapshots = "Permissions.Management.Snapshots.Read";
    public const string CheckFeaturesInternal = "Permissions.Internal.FeatureCheck";
    public const string ReadCapabilityMetadataInternal = "Permissions.Internal.CapabilityMetadata.Read";
    public const string ProvisionTenantEntitlementsInternal = "Permissions.Internal.Entitlements.Provision";
}

public static class PermissionsScopes
{
    public const string EvaluatePermissions = "permissions";
    public const string CheckFeaturesInternal = "features.check";
    public const string ReadCapabilityMetadataInternal = "capabilities.read";
    public const string SubjectPermissionAuthorizationsInternal = "permissions-internal";
    public const string ProvisionTenantEntitlementsInternal = "entitlements.provision";
}

public static class PermissionsHostPolicies
{
    public const string EvaluatePermissionsScope = "PermissionsHost.EvaluateScope";
    public const string SubjectPermissionAuthorizationInternal = "PermissionsHost.SubjectPermissionAuthorizationInternal";
    public const string ProvisionTenantEntitlementsInternal = "PermissionsHost.ProvisionTenantEntitlementsInternal";
}
