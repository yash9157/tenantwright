using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.Grants;

[Audited]
public class PermissionGrant : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public string Name { get; private set; } = default!;

    public string ProviderName { get; private set; } = default!;

    public string ProviderKey { get; private set; } = default!;

    public PermissionGrantEffect Effect { get; private set; }

    public DateTime CreationTimeUtc { get; private set; }

    /// <summary>
    /// Optional expiry for a time-bound grant. Null means the grant never expires. Enforced at
    /// evaluation time (<c>PermissionSnapshotBuilder</c> excludes expired rows from the snapshot
    /// query) and swept by <c>PermissionGrantSweepWorker</c>, which hard-deletes expired rows and
    /// bumps the provider stamp so cached snapshots are invalidated promptly.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; private set; }

    /// <summary>Optional free-text reason recorded for the grant (e.g. change-management justification).</summary>
    public string? Reason { get; private set; }

    /// <summary>Optional external ticket/change-request identifier associated with the grant.</summary>
    public string? TicketId { get; private set; }

    /// <summary>The user who created or last updated this grant, when known (system/seed grants may be null).</summary>
    public Guid? GrantedByUserId { get; private set; }

    protected PermissionGrant()
    {
    }

    public PermissionGrant(
        Guid id,
        Guid? tenantId,
        string name,
        string providerName,
        string providerKey,
        PermissionGrantEffect effect,
        DateTime creationTimeUtc,
        DateTime? expiresAtUtc = null,
        string? reason = null,
        string? ticketId = null,
        Guid? grantedByUserId = null)
        : base(id)
    {
        TenantId = tenantId;
        Name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: PermissionsConsts.MaxPermissionNameLength);
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: PermissionsConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionsConsts.MaxProviderKeyLength);
        Effect = effect;
        CreationTimeUtc = creationTimeUtc;
        ExpiresAtUtc = expiresAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : Check.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: PermissionGrantConsts.MaxReasonLength);
        TicketId = string.IsNullOrWhiteSpace(ticketId) ? null : Check.NotNullOrWhiteSpace(ticketId, nameof(ticketId), maxLength: PermissionGrantConsts.MaxTicketIdLength);
        GrantedByUserId = grantedByUserId;
    }

    public void SetEffect(PermissionGrantEffect effect)
    {
        Effect = effect;
    }

    public void SetProviderKey(string providerKey)
    {
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionsConsts.MaxProviderKeyLength);
    }

    public void SetLifecycleMetadata(DateTime? expiresAtUtc, string? reason, string? ticketId, Guid? grantedByUserId)
    {
        ExpiresAtUtc = expiresAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : Check.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: PermissionGrantConsts.MaxReasonLength);
        TicketId = string.IsNullOrWhiteSpace(ticketId) ? null : Check.NotNullOrWhiteSpace(ticketId, nameof(ticketId), maxLength: PermissionGrantConsts.MaxTicketIdLength);
        GrantedByUserId = grantedByUserId;
    }

    public bool IsExpired(DateTime utcNow)
    {
        return ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= utcNow;
    }
}
