using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.AuditLogs;

public class AuditLogsPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(AuditLogsPermissions.GroupName, "Audit Logs");
        var root = group.AddPermission(AuditLogsPermissions.Default, "Audit Logs");

        root.AddChild(AuditLogsPermissions.Read, "Read Audit Logs");
        root.AddChild(AuditLogsPermissions.ReadDetails, "Read Audit Log Details");

        var sensitivePayload = root.AddChild(AuditLogsPermissions.SensitivePayload.Default, "Sensitive Audit Payloads");
        sensitivePayload.AddChild(AuditLogsPermissions.SensitivePayload.Read, "Read Sensitive Audit Payloads");

        var entityChanges = root.AddChild(AuditLogsPermissions.EntityChanges.Default, "Entity Changes");
        entityChanges.AddChild(AuditLogsPermissions.EntityChanges.Read, "Read Entity Change History");

        root.AddChild(AuditLogsPermissions.Export, "Export Audit Logs");

        var archive = root.AddChild(AuditLogsPermissions.Archive.Default, "Archive Audit Logs");
        archive.AddChild(AuditLogsPermissions.Archive.Read, "Read Archive Manifests and Restore Jobs");
        archive.AddChild(AuditLogsPermissions.Archive.Restore, "Request and Manage Archive Restores");
        archive.AddChild(AuditLogsPermissions.Archive.VerifyIntegrity, "Verify Archive Integrity");

        var legalHold = root.AddChild(AuditLogsPermissions.LegalHold.Default, "Legal Holds");
        legalHold.AddChild(AuditLogsPermissions.LegalHold.Read, "Read Legal Holds");
        legalHold.AddChild(AuditLogsPermissions.LegalHold.Create, "Create Legal Holds");
        legalHold.AddChild(AuditLogsPermissions.LegalHold.Release, "Release Legal Holds");
    }
}
