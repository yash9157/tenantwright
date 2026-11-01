using Microsoft.AspNetCore.Authorization;
using Permissions.Application.Contracts.Authorization;
using Permissions.Controllers.PermissionEvaluation;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionEvaluationCurrentUserPolicyTests
{
    [Fact]
    public void Permission_evaluation_routes_should_split_batch_management_from_authenticated_current_user_permissions()
    {
        var controllerPolicies = typeof(PermissionEvaluationController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToArray();
        var batchPolicies = typeof(PermissionEvaluationController)
            .GetMethod(nameof(PermissionEvaluationController.EvaluateAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToArray();
        var currentUserPolicies = typeof(PermissionEvaluationController)
            .GetMethod(nameof(PermissionEvaluationController.GetMyPermissionsAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        Assert.Empty(controllerPolicies);
        Assert.Equal(
            new[] { PermissionsHostPolicies.EvaluatePermissionsScope, PermissionsPolicies.EvaluatePermissions },
            batchPolicies);
        var currentUserAuthorize = Assert.Single(currentUserPolicies);
        Assert.Null(currentUserAuthorize.Policy);
    }

    [Fact]
    public void Current_principal_explanation_route_requires_authentication_without_management_evaluation_permission()
    {
        var policies = typeof(PermissionEvaluationController)
            .GetMethod(nameof(PermissionEvaluationController.ExplainMyPermissionAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        var authorize = Assert.Single(policies);
        Assert.Null(authorize.Policy);
    }

    [Fact]
    public void Explain_route_should_require_the_same_policy_as_batch_evaluation()
    {
        var explainPolicies = typeof(PermissionEvaluationController)
            .GetMethod(nameof(PermissionEvaluationController.ExplainAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToArray();

        Assert.Equal(
            new[] { PermissionsHostPolicies.EvaluatePermissionsScope, PermissionsPolicies.EvaluatePermissions },
            explainPolicies);
    }
}
