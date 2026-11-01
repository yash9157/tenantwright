using Permissions.Application.Contracts.Definitions;
using Xunit;

namespace Shared.Permissions.Tests;

public sealed class PermissionDelegationMetadataTests
{
    [Theory]
    [InlineData(0.001)]
    [InlineData(1.5)]
    [InlineData(2147483646.5)]
    public void Constructor_fractional_second_maximum_expiry_should_reject_unrepresentable_precision(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateMetadata(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Constructor_whole_second_maximum_expiry_at_persistence_boundary_should_preserve_value()
    {
        var metadata = CreateMetadata(TimeSpan.FromSeconds(int.MaxValue));

        Assert.Equal(TimeSpan.FromSeconds(int.MaxValue), metadata.MaximumExpiry);
    }

    [Fact]
    public void ConfigureDelegation_resource_allow_without_approval_preserves_bounded_server_owned_metadata()
    {
        var permission = new PermissionDefinition("Notifications.Templates.Publish", "Publish templates");

        permission.ConfigureDelegation(new PermissionDelegationMetadata(
            owningService: "Notifications",
            managerPermissionName: "Notifications.ManageAccess",
            approverPermissionName: null,
            allowedTargetKinds: PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role,
            allowedScopeKinds: PermissionDelegationScopeKinds.Resource,
            allowedEffects: PermissionDelegationEffects.Allow,
            maximumExpiry: TimeSpan.FromDays(30),
            requiresApproval: false,
            reviewCadence: TimeSpan.FromDays(90),
            requiredFeatureName: null,
            delegable: true));

        var metadata = Assert.IsType<PermissionDelegationMetadata>(permission.Delegation);
        Assert.Equal("Notifications", metadata.OwningService);
        Assert.Equal(PermissionDelegationScopeKinds.Resource, metadata.AllowedScopeKinds);
        Assert.Equal(PermissionDelegationEffects.Allow, metadata.AllowedEffects);
        Assert.Equal(TimeSpan.FromDays(30), metadata.MaximumExpiry);
        Assert.Equal(TimeSpan.FromDays(90), metadata.ReviewCadence);
    }

    [Theory]
    [InlineData((short)2)]
    [InlineData((short)8)]
    [InlineData((short)(4 | 8))]
    public void Constructor_unknown_scope_kind_bits_should_be_rejected(short scopeKinds)
    {
        Assert.Throws<ArgumentException>(() => new PermissionDelegationMetadata(
            owningService: "Notifications",
            managerPermissionName: "Notifications.ManageAccess",
            approverPermissionName: null,
            allowedTargetKinds: PermissionDelegationTargetKinds.User,
            allowedScopeKinds: (PermissionDelegationScopeKinds)scopeKinds,
            allowedEffects: PermissionDelegationEffects.Allow,
            maximumExpiry: null,
            requiresApproval: false,
            reviewCadence: null,
            requiredFeatureName: null,
            delegable: true));
    }

    private static PermissionDelegationMetadata CreateMetadata(TimeSpan maximumExpiry) => new(
        owningService: "Notifications",
        managerPermissionName: "Notifications.ManageAccess",
        approverPermissionName: null,
        allowedTargetKinds: PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role,
        allowedScopeKinds: PermissionDelegationScopeKinds.Resource,
        allowedEffects: PermissionDelegationEffects.Allow,
        maximumExpiry: maximumExpiry,
        requiresApproval: false,
        reviewCadence: TimeSpan.FromDays(90),
        requiredFeatureName: null,
        delegable: true);
}
