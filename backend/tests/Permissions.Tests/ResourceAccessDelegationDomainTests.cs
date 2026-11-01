using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

public sealed class ResourceAccessDelegationDomainTests
{
    [Fact]
    public void Published_descriptor_is_immutable_and_requires_complete_policy()
    {
        var definition = new PermissionDelegationDefinition(Guid.CreateVersion7(), "Workspaces.Read", "Workspaces");
        var version = new PermissionDelegationDefinitionVersion(Guid.CreateVersion7(), definition.Id, 1,
            new string('a', 64), "Workspaces.ManageAccess", PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role,
            PermissionDelegationScopeKinds.Resource,
            PermissionDelegationEffects.Allow | PermissionDelegationEffects.Deny, 3600, true,
            "Permissions.TenantDelegation.Approvals.Decide", null, null, false, DateTime.UtcNow, null);

        version.Publish();
        definition.Publish(version.Id);

        Assert.Equal(PermissionDelegationDefinitionPublicationState.Published, version.PublicationState);
        Assert.Equal(version.Id, definition.CurrentVersionId);
        Assert.Throws<InvalidOperationException>(() => version.Publish());
        Assert.Throws<ArgumentException>(() => new PermissionDelegationDefinitionVersion(Guid.CreateVersion7(), definition.Id, 2,
            new string('b', 64), "Workspaces.ManageAccess", PermissionDelegationTargetKinds.User,
            PermissionDelegationScopeKinds.Resource, PermissionDelegationEffects.Allow, null, true, null, null, null, false, DateTime.UtcNow, null));
    }

    [Theory]
    [InlineData((short)2)]
    [InlineData((short)8)]
    [InlineData((short)(4 | 8))]
    public void Delegation_version_rejects_unknown_scope_kind_bits(short scopeKinds)
    {
        Assert.Throws<ArgumentException>(() => new PermissionDelegationDefinitionVersion(Guid.CreateVersion7(), Guid.CreateVersion7(), 1,
            new string('c', 64), "Workspaces.ManageAccess", PermissionDelegationTargetKinds.User,
            (PermissionDelegationScopeKinds)scopeKinds, PermissionDelegationEffects.Allow, null, false, null, null, null, true,
            DateTime.UtcNow, null));
    }

    [Fact]
    public void Resource_grant_requires_a_complete_live_owner_receipt()
    {
        var now = DateTime.UtcNow;
        var grant = new ResourcePermissionGrant(Guid.CreateVersion7(), Guid.CreateVersion7(), "Workspaces.Read", "workspace-desk",
            Guid.CreateVersion7().ToString("D"), 4, "User", Guid.CreateVersion7().ToString("D"), PermissionGrantEffect.Allow,
            now, null, "access", null, Guid.CreateVersion7());

        Assert.Throws<ArgumentException>(() => grant.BindOwnerValidatedResourceScope(PermissionDelegationScopeKinds.Resource,
            "Workspaces", Guid.CreateVersion7(), Guid.Empty, 1, now.AddMinutes(5), ResourceAccessApprovalState.Approved,
            ResourceAccessReviewState.NotRequired, null, null));

        grant.BindOwnerValidatedResourceScope(PermissionDelegationScopeKinds.Resource,
            "Workspaces", Guid.CreateVersion7(), Guid.CreateVersion7(), 9, now.AddMinutes(5), ResourceAccessApprovalState.Approved,
            ResourceAccessReviewState.Current, now.AddDays(30), null);

        Assert.Equal(PermissionGrantScopeKind.Resource, grant.GrantScopeKind);
        Assert.Equal(ResourceAccessApprovalState.Approved, grant.ApprovalState);
        Assert.Equal(ResourceAccessReviewState.Current, grant.ReviewState);
    }
}
