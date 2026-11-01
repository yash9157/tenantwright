namespace Shared.AuditLogs.Auditing;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property)]
public sealed class AuditedAttribute : Attribute
{
    /// <summary>
    /// Optional audit category for this action/controller (e.g. "auth", "permissions.grant",
    /// "break-glass"). Overrides the default <c>"http.request"</c> category on the resulting
    /// <see cref="AuditLogInfo"/>. Lets security-critical endpoints opt into
    /// <see cref="SharedAuditingOptions.StrictFailureCategories"/> fail-closed enforcement (sub-flow 3)
    /// without affecting every other audited endpoint.
    /// </summary>
    public string? Category { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property)]
public sealed class DisableAuditingAttribute : Attribute
{
    public bool UpdateModificationProps { get; set; } = true;
}

public interface IAuditingEnabled
{
}
