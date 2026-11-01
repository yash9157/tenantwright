using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.Permissions;

public class PermissionsPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(PermissionsPermissions.GroupName, "Permissions");

        var root = group.AddPermission(PermissionsPermissions.Management.Default, "Permission Management");
        // Escalation-capable permissions are approval-gated (flow 6): granting them creates a Pending
        // request a different admin must approve. Approve itself is deliberately NOT gated, to avoid a
        // bootstrap deadlock (you would need an approver to grant the first approver).
        root.AddChild(PermissionsPermissions.Management.Grants, "Manage Grants").RequireApproval();
        var approve = root.AddChild(PermissionsPermissions.Management.Approve.Default, "Sensitive Grant Approvals");
        approve.AddChild(PermissionsPermissions.Management.Approve.Read, "Read Sensitive Grant Approvals");
        approve.AddChild(PermissionsPermissions.Management.Approve.Decide, "Decide Sensitive Grant Approvals");
        approve.AddChild(PermissionsPermissions.Management.Approve.CancelAny, "Cancel Any Sensitive Grant Request");
        root.AddChild(PermissionsPermissions.Management.History, "Read Permission Grant History");
        var catalog = root.AddChild(PermissionsPermissions.Management.Catalog.Default, "Permission Catalog");
        catalog.AddChild(PermissionsPermissions.Management.Catalog.Read, "Read Permission Catalog");
        catalog.AddChild(PermissionsPermissions.Management.Catalog.Create, "Create Permission Catalog Nodes");
        catalog.AddChild(PermissionsPermissions.Management.Catalog.Update, "Update Permission Catalog Nodes");
        catalog.AddChild(PermissionsPermissions.Management.Catalog.Delete, "Delete Permission Catalog Nodes");

        var conflictRules = root.AddChild(PermissionsPermissions.Management.ConflictRules.Default, "Separation-of-Duty Conflict Rules");
        conflictRules.AddChild(PermissionsPermissions.Management.ConflictRules.Read, "Read Conflict Rules");
        conflictRules.AddChild(PermissionsPermissions.Management.ConflictRules.Create, "Create Conflict Rules").RequireApproval();
        conflictRules.AddChild(PermissionsPermissions.Management.ConflictRules.Update, "Update Conflict Rules").RequireApproval();
        conflictRules.AddChild(PermissionsPermissions.Management.ConflictRules.Delete, "Delete Conflict Rules").RequireApproval();
        conflictRules.AddChild(PermissionsPermissions.Management.ConflictRules.Decide, "Decide Conflict Rule Changes");

        var features = root.AddChild(PermissionsPermissions.Management.Features.Default, "Permission Features");
        features.AddChild(PermissionsPermissions.Management.Features.Read, "Read Permission Features");
        features.AddChild(PermissionsPermissions.Management.Features.Update, "Update Permission Features");
        root.AddChild(PermissionsPermissions.Management.Evaluate, "Evaluate Permissions");
        root.AddChild(PermissionsPermissions.Management.Review, "Review Tenant Access").RequireApproval();
        var importExport = root.AddChild(PermissionsPermissions.Management.ImportExport.Default, "Permission Transfers");
        importExport.AddChild(PermissionsPermissions.Management.ImportExport.Read, "Read Permission Transfer History");
        importExport.AddChild(PermissionsPermissions.Management.ImportExport.Export, "Export Permissions");
        importExport.AddChild(PermissionsPermissions.Management.ImportExport.Validate, "Validate Permission Imports");
        importExport.AddChild(PermissionsPermissions.Management.ImportExport.Apply, "Apply Permission Imports").RequireApproval();

        var snapshots = root.AddChild(PermissionsPermissions.Management.Snapshots.Default, "Permission Snapshots");
        snapshots.AddChild(PermissionsPermissions.Management.Snapshots.Read, "Read Permission Snapshots");

        var tenantAccess = group.AddPermission(PermissionsPermissions.TenantAccess.Default, "Tenant Permission Access");
        tenantAccess.AddChild(PermissionsPermissions.TenantAccess.Read, "Read Tenant Permission Access");
        tenantAccess.AddChild(PermissionsPermissions.TenantAccess.Manage, "Manage Tenant Permission Delegation");

        var platformDocumentation = group.AddPermission(
            PermissionsPermissions.PlatformDocumentation.Default,
            "Platform Documentation");
        platformDocumentation.AddChild(
            PermissionsPermissions.PlatformDocumentation.Read,
            "Read Internal Platform Documentation");
    }
}
