using Permissions.Application.Caching;
using Permissions.Application.Evaluation;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

public class PermissionDecisionEngineTests
{
    private readonly PermissionDecisionEngine _engine = new();

    [Fact]
    public void UserDeny_Wins_Over_Everything()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserDenyExact = ["Project.TenantWright.Documents.Read"],
            UserAllowExact = ["Project.TenantWright.Documents.Read"],
            RoleAllowExact = ["Project.TenantWright.Documents.Read"],
            RoleDenyExact = Array.Empty<string>()
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.False(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.UserExplicitDeny, decision.Source);
    }

    [Fact]
    public void UserAllow_Wins_Over_RoleDeny()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            UserAllowExact = ["Project.TenantWright.Documents.Update"],
            RoleDenyExact = ["Project.TenantWright.Documents.Update"]
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Update");

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.UserExplicitAllow, decision.Source);
    }

    [Fact]
    public void WildcardAllow_Grants_Children()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            RoleAllowWildcard = ["Project.TenantWright.*"]
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.WildcardAllow, decision.Source);
    }

    [Fact]
    public void InheritedAllow_Grants_Child_When_Parent_Allowed()
    {
        var snapshot = new PermissionSnapshotCacheItem
        {
            RoleAllowExact = ["Project.TenantWright.Documents"]
        };

        var decision = _engine.Evaluate(snapshot, "Project.TenantWright.Documents.Read");

        Assert.True(decision.IsGranted);
        Assert.Equal(PermissionDecisionSource.InheritedAllow, decision.Source);
    }
}
