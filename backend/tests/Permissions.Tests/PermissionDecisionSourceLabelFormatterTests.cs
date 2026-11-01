using Permissions.Application.Evaluation;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionDecisionSourceLabelFormatterTests
{
    [Theory]
    [InlineData(PermissionDecisionSource.UserExplicitAllow, "User explicit allow")]
    [InlineData(PermissionDecisionSource.RoleExplicitAllow, "Role explicit allow")]
    [InlineData(PermissionDecisionSource.FeatureDenied, "Feature denied")]
    [InlineData(PermissionDecisionSource.NotGranted, "Not granted")]
    public void Format_returns_stable_user_facing_label(PermissionDecisionSource source, string expected)
    {
        var result = PermissionDecisionSourceLabelFormatter.Format(source);

        Assert.Equal(expected, result);
    }
}
