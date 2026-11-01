using Permissions.Application.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionAssignmentProviderKeysTests
{
    [Fact]
    public void Normalize_role_key_returns_canonical_immutable_provider_key()
    {
        var roleId = Guid.Parse("9a5f07b0-664b-4be4-a1f1-d6d1a02cc146");
        var result = PermissionProviderKeyNormalizer.Normalize(PermissionProviderNames.Role, roleId.ToString("D"));

        Assert.Equal("9a5f07b0-664b-4be4-a1f1-d6d1a02cc146", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TenantAdmin")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("9a5f07b0664b4be4a1f1d6d1a02cc146")]
    public void Normalize_role_key_rejects_noncanonical_or_empty_values(string providerKey)
    {
        Assert.Throws<ArgumentException>(() => PermissionProviderKeyNormalizer.Normalize(PermissionProviderNames.Role, providerKey));
    }

    [Fact]
    public void Normalize_user_key_returns_guid_provider_key()
    {
        var userId = Guid.Parse("9a5f07b0-664b-4be4-a1f1-d6d1a02cc146");

        var result = PermissionAssignmentProviderKeys.NormalizeUserKey(userId);

        Assert.Equal("9a5f07b0-664b-4be4-a1f1-d6d1a02cc146", result);
    }

    [Fact]
    public void Normalize_user_key_rejects_empty_user_id()
    {
        Assert.Throws<ArgumentException>(() => PermissionAssignmentProviderKeys.NormalizeUserKey(Guid.Empty));
    }
}
