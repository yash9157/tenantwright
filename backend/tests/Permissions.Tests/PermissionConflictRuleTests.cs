using Permissions.Application.Caching;
using Permissions.Application.Evaluation;
using Permissions.Application.Grants;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Coverage for sub-flow 5 (separation-of-duty conflict rules): the <see cref="PermissionConflictRule"/>
/// entity normalizes its pair, <see cref="PermissionConflictRuleService"/> builds a symmetric lookup, and
/// <see cref="PermissionDecisionEngine"/>'s conflict-aware overload denies both sides defensively when a
/// snapshot somehow grants a conflicting pair.
/// </summary>
public sealed class PermissionConflictRuleTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly PermissionDecisionEngine _engine = new();

    [Fact]
    public void PermissionConflictRule_normalizes_the_pair_regardless_of_argument_order()
    {
        var ruleAB = new PermissionConflictRule(Guid.NewGuid(), TenantId, "Payments.Approve", "Payments.Create", DateTime.UtcNow);
        var ruleBA = new PermissionConflictRule(Guid.NewGuid(), TenantId, "Payments.Create", "Payments.Approve", DateTime.UtcNow);

        Assert.Equal(ruleAB.PermissionA, ruleBA.PermissionA);
        Assert.Equal(ruleAB.PermissionB, ruleBA.PermissionB);
    }

    [Fact]
    public void PermissionConflictRule_rejects_a_permission_conflicting_with_itself()
    {
        Assert.Throws<ArgumentException>(() =>
            new PermissionConflictRule(Guid.NewGuid(), TenantId, "Payments.Approve", "Payments.Approve", DateTime.UtcNow));
    }

    [Fact]
    public void FindConflict_detects_a_conflict_regardless_of_which_side_is_queried()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Payments.Approve"] = new(StringComparer.Ordinal) { "Payments.Create" },
            ["Payments.Create"] = new(StringComparer.Ordinal) { "Payments.Approve" }
        };

        var conflictForApprove = PermissionConflictRuleService.FindConflict(map, "Payments.Approve", ["Payments.Create", "Reports.Read"]);
        var conflictForCreate = PermissionConflictRuleService.FindConflict(map, "Payments.Create", ["Payments.Approve"]);

        Assert.Equal("Payments.Create", conflictForApprove);
        Assert.Equal("Payments.Approve", conflictForCreate);
    }

    [Fact]
    public void FindConflict_returns_null_for_unrelated_permissions()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Payments.Approve"] = new(StringComparer.Ordinal) { "Payments.Create" }
        };

        var conflict = PermissionConflictRuleService.FindConflict(map, "Reports.Read", ["Payments.Create", "Payments.Approve"]);

        Assert.Null(conflict);
    }

    [Fact]
    public void FindConflict_ignores_the_permission_itself_in_the_held_set()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Payments.Approve"] = new(StringComparer.Ordinal) { "Payments.Create" }
        };

        var conflict = PermissionConflictRuleService.FindConflict(map, "Payments.Approve", ["Payments.Approve"]);

        Assert.Null(conflict);
    }

    [Fact]
    public void Evaluate_denies_both_sides_of_a_conflicting_pair_when_both_are_somehow_granted()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserAllowExact = ["Payments.Approve", "Payments.Create"]
        };
        var conflictMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Payments.Approve"] = new(StringComparer.Ordinal) { "Payments.Create" },
            ["Payments.Create"] = new(StringComparer.Ordinal) { "Payments.Approve" }
        };

        var approveDecision = _engine.Evaluate(snapshot, "Payments.Approve", conflictMap);
        var createDecision = _engine.Evaluate(snapshot, "Payments.Create", conflictMap);

        Assert.False(approveDecision.IsGranted);
        Assert.Equal(PermissionDecisionSource.ConflictDenied, approveDecision.Source);
        Assert.Equal("Payments.Create", approveDecision.MatchedRule);

        Assert.False(createDecision.IsGranted);
        Assert.Equal(PermissionDecisionSource.ConflictDenied, createDecision.Source);
        Assert.Equal("Payments.Approve", createDecision.MatchedRule);
    }

    [Fact]
    public void Evaluate_does_not_deny_when_only_one_side_of_the_conflicting_pair_is_granted()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserAllowExact = ["Payments.Approve"]
        };
        var conflictMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Payments.Approve"] = new(StringComparer.Ordinal) { "Payments.Create" },
            ["Payments.Create"] = new(StringComparer.Ordinal) { "Payments.Approve" }
        };

        var decision = _engine.Evaluate(snapshot, "Payments.Approve", conflictMap);

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.UserExplicitAllow, decision.Source);
    }

    [Fact]
    public void Evaluate_leaves_unrelated_permissions_unaffected_by_a_conflict_rule()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserAllowExact = ["Payments.Approve", "Payments.Create", "Reports.Read"]
        };
        var conflictMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Payments.Approve"] = new(StringComparer.Ordinal) { "Payments.Create" },
            ["Payments.Create"] = new(StringComparer.Ordinal) { "Payments.Approve" }
        };

        var reportsDecision = _engine.Evaluate(snapshot, "Reports.Read", conflictMap);

        Assert.True(reportsDecision.IsGranted);
        Assert.Equal(PermissionDecisionSource.UserExplicitAllow, reportsDecision.Source);
    }

    [Fact]
    public void Evaluate_without_a_conflict_map_behaves_like_the_unconflicted_overload()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserAllowExact = ["Payments.Approve"]
        };

        var decision = _engine.Evaluate(snapshot, "Payments.Approve", conflictMap: null);

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.UserExplicitAllow, decision.Source);
    }
}
