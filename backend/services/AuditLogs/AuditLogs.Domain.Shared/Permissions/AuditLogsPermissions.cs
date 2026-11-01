namespace Shared.Permissions.Definitions.AuditLogs;

public static class AuditLogsPermissions
{
    public const string GroupName = "AuditLogs";

    public const string Default = "AuditLogs";

    public const string Read = "AuditLogs.Read";

    public const string ReadDetails = "AuditLogs.ReadDetails";

    public static class SensitivePayload
    {
        public const string Default = "AuditLogs.SensitivePayload";
        public const string Read = "AuditLogs.SensitivePayload.Read";
    }

    public static class EntityChanges
    {
        public const string Default = "AuditLogs.EntityChanges";
        public const string Read = "AuditLogs.EntityChanges.Read";
    }

    public const string Export = "AuditLogs.Export";

    public static class Archive
    {
        public const string Default = "AuditLogs.Archive";
        public const string Read = "AuditLogs.Archive.Read";
        public const string Restore = "AuditLogs.Archive.Restore";
        public const string VerifyIntegrity = "AuditLogs.Archive.VerifyIntegrity";
    }

    public static class LegalHold
    {
        public const string Default = "AuditLogs.LegalHold";
        public const string Read = "AuditLogs.LegalHold.Read";
        public const string Create = "AuditLogs.LegalHold.Create";
        public const string Release = "AuditLogs.LegalHold.Release";
    }
}
