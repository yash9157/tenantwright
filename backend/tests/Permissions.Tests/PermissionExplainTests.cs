using Permissions.Application.Caching;
using Permissions.Application.Evaluation;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionExplainTests
{
    private readonly PermissionDecisionEngine _engine = new();

    [Fact]
    public void Explain_should_report_UserExplicitDeny_with_matched_rule_for_a_denied_user()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserDenyExact = ["Project.TenantWright.Documents.Read"],
            RoleAllowExact = ["Project.TenantWright.Documents.Read"]
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.False(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.UserExplicitDeny, decision.Source);
        Assert.Equal("Project.TenantWright.Documents.Read", decision.MatchedRule);
        Assert.Equal("User explicit deny", PermissionDecisionSourceLabelFormatter.Format(decision.Source));
    }

    [Fact]
    public void Explain_should_report_InheritedAllow_with_the_parent_rule_that_matched()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            RoleAllowExact = ["Project.TenantWright.Documents"]
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.InheritedAllow, decision.Source);
        Assert.Equal("Project.TenantWright.Documents", decision.MatchedRule);
        Assert.Equal("Inherited allow", PermissionDecisionSourceLabelFormatter.Format(decision.Source));
    }

    [Fact]
    public void Explain_should_report_NotGranted_with_no_matched_rule_by_default()
    {
        var snapshot = new PermissionSnapshotCacheItem();

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.False(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.NotGranted, decision.Source);
        Assert.Null(decision.MatchedRule);
        Assert.Equal("Not granted", PermissionDecisionSourceLabelFormatter.Format(decision.Source));
    }

    [Fact]
    public void Explain_should_report_the_specific_wildcard_pattern_that_matched()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            RoleAllowWildcard = ["Project.TenantWright.*"]
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.WildcardAllow, decision.Source);
        Assert.Equal("Project.TenantWright.*", decision.MatchedRule);
    }
}
