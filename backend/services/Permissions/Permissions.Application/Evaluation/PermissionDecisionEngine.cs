using Permissions.Application.Caching;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Evaluation;

public class PermissionDecisionEngine : ISingletonDependency
{
    public PermissionDecision Evaluate(PermissionSnapshotCacheItem snapshot, string permissionName)
    {
        permissionName = permissionName.Trim();

        if (Contains(snapshot.UserDenyExact, permissionName))
        {
            return new PermissionDecision(false, PermissionDecisionSource.UserExplicitDeny, permissionName);
        }

        if (MatchesAny(snapshot.UserDenyWildcard, permissionName, out var userDenyPattern))
        {
            return new PermissionDecision(false, PermissionDecisionSource.WildcardDeny, userDenyPattern);
        }

        if (Contains(snapshot.UserAllowExact, permissionName))
        {
            return new PermissionDecision(true, PermissionDecisionSource.UserExplicitAllow, permissionName);
        }

        if (MatchesAny(snapshot.UserAllowWildcard, permissionName, out var userAllowPattern))
        {
            return new PermissionDecision(true, PermissionDecisionSource.WildcardAllow, userAllowPattern);
        }

        if (Contains(snapshot.RoleDenyExact, permissionName))
        {
            return new PermissionDecision(false, PermissionDecisionSource.RoleExplicitDeny, permissionName);
        }

        if (MatchesAny(snapshot.RoleDenyWildcard, permissionName, out var roleDenyPattern))
        {
            return new PermissionDecision(false, PermissionDecisionSource.WildcardDeny, roleDenyPattern);
        }

        if (Contains(snapshot.RoleAllowExact, permissionName))
        {
            return new PermissionDecision(true, PermissionDecisionSource.RoleExplicitAllow, permissionName);
        }

        if (MatchesAny(snapshot.RoleAllowWildcard, permissionName, out var roleAllowPattern))
        {
            return new PermissionDecision(true, PermissionDecisionSource.WildcardAllow, roleAllowPattern);
        }

        foreach (var parent in PermissionPatternHelper.EnumerateParents(permissionName))
        {
            if (Contains(snapshot.UserDenyExact, parent))
            {
                return new PermissionDecision(false, PermissionDecisionSource.WildcardDeny, parent);
            }

            if (Contains(snapshot.RoleDenyExact, parent))
            {
                return new PermissionDecision(false, PermissionDecisionSource.WildcardDeny, parent);
            }

            if (Contains(snapshot.UserAllowExact, parent))
            {
                return new PermissionDecision(true, PermissionDecisionSource.InheritedAllow, parent);
            }

            if (Contains(snapshot.RoleAllowExact, parent))
            {
                return new PermissionDecision(true, PermissionDecisionSource.InheritedAllow, parent);
            }

            var parentWildcard = $"{parent}.*";
            if (MatchesAny(snapshot.UserAllowWildcard, parentWildcard, out var userParentPattern))
            {
                return new PermissionDecision(true, PermissionDecisionSource.InheritedAllow, userParentPattern);
            }

            if (MatchesAny(snapshot.RoleAllowWildcard, parentWildcard, out var roleParentPattern))
            {
                return new PermissionDecision(true, PermissionDecisionSource.InheritedAllow, roleParentPattern);
            }
        }

        return new PermissionDecision(false, PermissionDecisionSource.NotGranted, null);
    }

    /// <summary>
    /// Same as <see cref="Evaluate(PermissionSnapshotCacheItem, string)"/>, but additionally applies
    /// separation-of-duty conflict rules as a defensive check: if <paramref name="permissionName"/>
    /// would be granted and the same snapshot also grants a permission it conflicts with (per
    /// <paramref name="conflictMap"/>), both are denied rather than picking one side arbitrarily. This
    /// is the fallback for grants that predate a conflict rule, were imported, or reached this state via
    /// a wildcard; the primary control rejects the conflicting grant at grant time.
    /// </summary>
    public PermissionDecision Evaluate(
        PermissionSnapshotCacheItem snapshot,
        string permissionName,
        IReadOnlyDictionary<string, HashSet<string>>? conflictMap)
    {
        var decision = Evaluate(snapshot, permissionName);
        if (!decision.IsGranted || conflictMap == null || conflictMap.Count == 0)
        {
            return decision;
        }

        permissionName = permissionName.Trim();
        if (!conflictMap.TryGetValue(permissionName, out var conflicts) || conflicts.Count == 0)
        {
            return decision;
        }

        foreach (var conflictingPermission in conflicts)
        {
            if (Evaluate(snapshot, conflictingPermission).IsGranted)
            {
                return new PermissionDecision(false, PermissionDecisionSource.ConflictDenied, conflictingPermission);
            }
        }

        return decision;
    }

    private static bool Contains(string[] items, string value)
    {
        return Array.IndexOf(items, value) >= 0;
    }

    private static bool MatchesAny(string[] patterns, string permissionName, out string? matchedPattern)
    {
        foreach (var pattern in patterns)
        {
            if (PermissionPatternHelper.Matches(permissionName, pattern))
            {
                matchedPattern = pattern;
                return true;
            }
        }

        matchedPattern = null;
        return false;
    }
}

/// <summary>
/// The outcome of evaluating a single permission against a snapshot. <see cref="MatchedRule"/> is the
/// specific literal grant name or wildcard pattern that produced the decision (e.g. <c>Documents.Read</c>
/// or <c>Documents.*</c>), or <c>null</c> when no grant matched (<see cref="PermissionDecisionSource.NotGranted"/>).
/// </summary>
public readonly record struct PermissionDecision(bool IsGranted, PermissionDecisionSource Source, string? MatchedRule = null);
