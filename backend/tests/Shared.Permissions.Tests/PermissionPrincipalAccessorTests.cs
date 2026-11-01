using System.Security.Claims;
using Xunit;

namespace Shared.Permissions.Tests;

public class PermissionPrincipalAccessorTests
{
    [Fact]
    public void Finds_UserId_From_Sub()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString("D"))
        }, "test");
        var principal = new ClaimsPrincipal(identity);

        var type = typeof(Shared.Permissions.SharedPermissionsModule).Assembly.GetType("Shared.Permissions.Authorization.PermissionPrincipalAccessor", throwOnError: true)!;
        var found = type
            .GetMethod("FindUserId", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { principal });

        Assert.Equal(userId, (Guid?)found);
    }

    [Fact]
    public void ComputeRoleIdentityHash_is_stable_for_the_same_role_ids()
    {
        var firstRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var type = typeof(Shared.Permissions.SharedPermissionsModule).Assembly.GetType("Shared.Permissions.Authorization.PermissionPrincipalAccessor", throwOnError: true)!;
        var method = type.GetMethod(
            "ComputeRoleIdentityHash",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        var first = method.Invoke(null, new object[] { new List<Guid> { secondRoleId, firstRoleId } });
        var second = method.Invoke(null, new object[] { new List<Guid> { firstRoleId, secondRoleId, firstRoleId } });

        Assert.Equal(first, second);
    }
}
