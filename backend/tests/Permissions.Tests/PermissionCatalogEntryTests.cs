using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.Catalog;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionCatalogEntryTests
{
    [Fact]
    public void Dynamic_entry_normalizes_metadata_and_advances_version_on_update()
    {
        var node = new PermissionNode(
            Guid.NewGuid(),
            tenantId: null,
            name: "Identity.Users.Manage",
            elementType: PermissionElementType.Api,
            elementKey: "identity/users",
            groupName: "Identity",
            displayName: "Users API",
            description: "Manage user access",
            parentName: "Identity.Users",
            requiredFeatures: [" feature-b ", "feature-a", "feature-a"],
            allowedProviders: ["Role"],
            allowedActions: ["Update"],
            sortOrder: 7);

        node.UpdateMetadata(
            "User administration",
            "Updated description",
            "Identity",
            "Identity.Users",
            isEnabled: false,
            allowsWildcard: false,
            requiresApproval: true,
            requiredFeatures: ["feature-c"],
            allowedProviders: ["User"],
            allowedActions: ["Read", "Update"],
            sortOrder: 8);

        Assert.Equal(2, node.Version);
        Assert.Equal(new[] { "feature-c" }, node.RequiredFeatures);
        Assert.Equal(new[] { "Read", "Update" }, node.AllowedActions);
        Assert.False(node.IsEnabled);
        Assert.True(node.RequiresApproval);
    }
}
