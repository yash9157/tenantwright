using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Constants;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.Grants;

/// <summary>
/// A separation-of-duty (SoD) constraint: a target (user/role) must never effectively hold both
/// <see cref="PermissionA"/> and <see cref="PermissionB"/> at the same time. Tenant-scoped so each
/// tenant defines its own conflicting pairs (e.g. "Payments.Approve" vs "Payments.Create" in a
/// finance workflow). The pair is stored in a normalized order (see
/// <see cref="PermissionConflictRuleKeyHelper.NormalizePair"/>) so "A vs B" and "B vs A" are treated
/// as the same rule and cannot be registered twice.
/// </summary>
[Audited]
public class PermissionConflictRule : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string PermissionA { get; private set; } = default!;
    public string PermissionB { get; private set; } = default!;
    public string? Reason { get; private set; }
    public DateTime CreationTimeUtc { get; private set; }

    /// <summary>Last time an approved change-request mutated this rule; null until first update.</summary>
    public DateTime? LastModifiedTimeUtc { get; private set; }

    /// <summary>Optimistic-concurrency version, bumped on every applied mutation.</summary>
    public long Version { get; private set; } = 1;

    protected PermissionConflictRule()
    {
    }

    public PermissionConflictRule(
        Guid id,
        Guid? tenantId,
        string permissionA,
        string permissionB,
        DateTime creationTimeUtc,
        string? reason = null)
        : base(id)
    {
        var (first, second) = NormalizeDistinctPair(permissionA, permissionB);

        TenantId = tenantId;
        PermissionA = first;
        PermissionB = second;
        Reason = NormalizeReason(reason);
        CreationTimeUtc = creationTimeUtc;
        Version = 1;
    }

    /// <summary>
    /// Applies an approved update: re-normalizes the pair, replaces the reason, and bumps
    /// <see cref="Version"/>. Callers must first verify the expected version to fail closed on
    /// concurrent edits.
    /// </summary>
    public void Update(string permissionA, string permissionB, string? reason, DateTime modifiedTimeUtc)
    {
        var (first, second) = NormalizeDistinctPair(permissionA, permissionB);
        PermissionA = first;
        PermissionB = second;
        Reason = NormalizeReason(reason);
        LastModifiedTimeUtc = modifiedTimeUtc;
        Version++;
    }

    private static (string First, string Second) NormalizeDistinctPair(string permissionA, string permissionB)
    {
        var (first, second) = PermissionConflictRuleKeyHelper.NormalizePair(
            Check.NotNullOrWhiteSpace(permissionA, nameof(permissionA), maxLength: PermissionsConsts.MaxPermissionNameLength),
            Check.NotNullOrWhiteSpace(permissionB, nameof(permissionB), maxLength: PermissionsConsts.MaxPermissionNameLength));

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new ArgumentException("A permission cannot conflict with itself.", nameof(permissionB));
        }

        return (first, second);
    }

    private static string? NormalizeReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? null
            : Check.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: PermissionGrantConsts.MaxReasonLength);
    }
}

/// <summary>
/// Normalizes a conflict pair to a stable (First, Second) ordinal order so a rule between two
/// permissions can be looked up or uniquely constrained regardless of which side is queried first.
/// </summary>
public static class PermissionConflictRuleKeyHelper
{
    public static (string First, string Second) NormalizePair(string permissionA, string permissionB)
    {
        permissionA = permissionA.Trim();
        permissionB = permissionB.Trim();

        return string.CompareOrdinal(permissionA, permissionB) <= 0
            ? (permissionA, permissionB)
            : (permissionB, permissionA);
    }
}
