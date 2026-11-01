using Shared.Permissions.Definitions.Tenant;
using Tenant.Domain.Shared.Enums;

namespace Tenant.Application.Tenants;

/// <summary>
/// Computes the per-tenant list-row capability tokens (<c>AllowedActions</c>) from a
/// row's status snapshot and the caller's resolved permission grants. Pure and
/// fail-closed: an action is emitted only when both its permission is granted and its
/// status precondition holds. Tokens are stable strings (never a renumberable enum) so
/// the wire contract cannot drift when actions are added or reordered.
///
/// Archive is intentionally coarse here (permission + not-deleted): the precise
/// close-before-archive gate is still enforced by the archive command on execute. This
/// avoids a per-row closure lookup on the hot list path.
/// </summary>
public static class TenantAllowedActionsCalculator
{
    public const string Rename = "rename";
    public const string Lifecycle = "lifecycle";
    public const string ConfigureConnection = "configureConnection";
    public const string TransferOwnership = "transferOwnership";
    public const string Restore = "restore";
    public const string Archive = "archive";

    /// <summary>
    /// The exact set of permissions the list needs to resolve for the caller. Resolve
    /// these once per request (batch) and pass the result to <see cref="Compute"/> for
    /// every row.
    /// </summary>
    public static IReadOnlyList<string> RequiredPermissions { get; } = new[]
    {
        TenantPermissions.Management.Update,
        TenantPermissions.Lifecycle.Manage,
        TenantPermissions.ConnectionStrings.Update,
        TenantPermissions.Ownership.Transfer,
        TenantPermissions.Management.Restore,
        TenantPermissions.Management.Archive
    };

    public static IReadOnlyList<string> Compute(
        TenantLifecycleStatus lifecycleStatus,
        bool isDeleted,
        Guid? ownerUserId,
        IReadOnlyDictionary<string, bool> grants)
    {
        bool Granted(string permission) => grants.TryGetValue(permission, out var value) && value;

        // Decommissioned is the only terminal lifecycle state with no valid transition,
        // so it is the single gate for rename/lifecycle on a live tenant.
        var notDecommissioned = lifecycleStatus != TenantLifecycleStatus.Decommissioned;

        var actions = new List<string>(RequiredPermissions.Count);

        if (isDeleted)
        {
            // A soft-deleted tenant only offers restore.
            if (Granted(TenantPermissions.Management.Restore))
            {
                actions.Add(Restore);
            }

            return actions;
        }

        if (notDecommissioned && Granted(TenantPermissions.Management.Update))
        {
            actions.Add(Rename);
        }

        if (notDecommissioned && Granted(TenantPermissions.Lifecycle.Manage))
        {
            actions.Add(Lifecycle);
        }

        if (Granted(TenantPermissions.ConnectionStrings.Update))
        {
            actions.Add(ConfigureConnection);
        }

        if (ownerUserId.HasValue && Granted(TenantPermissions.Ownership.Transfer))
        {
            actions.Add(TransferOwnership);
        }

        if (Granted(TenantPermissions.Management.Archive))
        {
            actions.Add(Archive);
        }

        return actions;
    }
}
