using Microsoft.EntityFrameworkCore;
using Permissions.Domain.Grants;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Grants;

/// <summary>
/// Loads tenant-scoped separation-of-duty (SoD) <see cref="PermissionConflictRule"/> pairs into a
/// lookup map and answers "does granting/holding X conflict with any of these already-held
/// permissions". Used both at grant time (reject the mutation) and defensively at evaluation time
/// (deny one side of an already-persisted conflicting pair).
/// </summary>
public class PermissionConflictRuleService : ITransientDependency
{
    private readonly IRepository<PermissionConflictRule, Guid> _repository;

    public PermissionConflictRuleService(IRepository<PermissionConflictRule, Guid> repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Builds a symmetric permission-name -> conflicting-permission-names map for the tenant, so a
    /// caller can look up either side of a rule without knowing which side was stored as A or B.
    /// </summary>
    public async Task<Dictionary<string, HashSet<string>>> BuildConflictMapAsync(Guid? tenantId, CancellationToken cancellationToken = default)
    {
        var queryable = await _repository.GetQueryableAsync();
        var rules = await queryable
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            AddConflict(map, rule.PermissionA, rule.PermissionB);
            AddConflict(map, rule.PermissionB, rule.PermissionA);
        }

        return map;
    }

    /// <summary>
    /// Returns the first permission in <paramref name="heldPermissions"/> that conflicts with
    /// <paramref name="permissionName"/> according to <paramref name="conflictMap"/>, or null if none.
    /// </summary>
    public static string? FindConflict(
        Dictionary<string, HashSet<string>> conflictMap,
        string permissionName,
        IEnumerable<string> heldPermissions)
    {
        if (!conflictMap.TryGetValue(permissionName, out var conflicts) || conflicts.Count == 0)
        {
            return null;
        }

        foreach (var held in heldPermissions)
        {
            if (string.Equals(held, permissionName, StringComparison.Ordinal))
            {
                continue;
            }

            if (conflicts.Contains(held))
            {
                return held;
            }
        }

        return null;
    }

    private static void AddConflict(Dictionary<string, HashSet<string>> map, string permission, string conflictsWith)
    {
        if (!map.TryGetValue(permission, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[permission] = set;
        }

        set.Add(conflictsWith);
    }
}
