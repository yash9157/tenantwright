using System.Reflection;
using Auth.Authorization;
using Auth.Controllers.Management;
using Auth.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Auth;
using Xunit;

namespace Auth.Tests;

public sealed class OpenIddictManagementContractTests
{
    [Fact]
    public void ManagementController_Exposes_Only_Canonical_OpenIddict_Route()
    {
        var routes = typeof(OpenIddictManagementController)
            .GetCustomAttributes<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .ToArray();

        Assert.Equal(new[] { "api/management/openiddict" }, routes);
        Assert.DoesNotContain("api/management/openidict", routes);
    }

    [Fact]
    public void ManagementGetActions_Expose_Typed_OpenApi_Responses()
    {
        AssertReturnType<OpenIddictManagementPage<OpenIddictApplicationModel>>(nameof(OpenIddictManagementController.GetApplicationListAsync));
        AssertReturnType<OpenIddictApplicationModel>(nameof(OpenIddictManagementController.GetApplicationAsync));
        AssertReturnType<OpenIddictManagementPage<OpenIddictScopeModel>>(nameof(OpenIddictManagementController.GetScopeListAsync));
        AssertReturnType<OpenIddictScopeModel>(nameof(OpenIddictManagementController.GetScopeAsync));
        AssertReturnType<OpenIddictManagementMetadataModel>(nameof(OpenIddictManagementController.GetMetadataAsync));
        AssertReturnType<OpenIddictApplicationImpactModel>(nameof(OpenIddictManagementController.GetApplicationImpactAsync));
    }

    [Theory]
    [InlineData(nameof(OpenIddictManagementController.GetApplicationListAsync), OpenIddictManagementPolicies.Applications.Read)]
    [InlineData(nameof(OpenIddictManagementController.GetApplicationAsync), OpenIddictManagementPolicies.Applications.Read)]
    [InlineData(nameof(OpenIddictManagementController.CreateApplicationAsync), OpenIddictManagementPolicies.Applications.Create)]
    [InlineData(nameof(OpenIddictManagementController.UpdateApplicationAsync), OpenIddictManagementPolicies.Applications.Update)]
    [InlineData(nameof(OpenIddictManagementController.RotateApplicationSecretAsync), OpenIddictManagementPolicies.Applications.RotateSecret)]
    [InlineData(nameof(OpenIddictManagementController.RevokeApplicationSecretAsync), OpenIddictManagementPolicies.Applications.RevokeSecret)]
    [InlineData(nameof(OpenIddictManagementController.DeleteApplicationAsync), OpenIddictManagementPolicies.Applications.Delete)]
    [InlineData(nameof(OpenIddictManagementController.GetScopeListAsync), OpenIddictManagementPolicies.Scopes.Read)]
    [InlineData(nameof(OpenIddictManagementController.GetScopeAsync), OpenIddictManagementPolicies.Scopes.Read)]
    [InlineData(nameof(OpenIddictManagementController.CreateScopeAsync), OpenIddictManagementPolicies.Scopes.Create)]
    [InlineData(nameof(OpenIddictManagementController.UpdateScopeAsync), OpenIddictManagementPolicies.Scopes.Update)]
    [InlineData(nameof(OpenIddictManagementController.DeleteScopeAsync), OpenIddictManagementPolicies.Scopes.Delete)]
    public void ManagementActions_Require_Exact_HostPolicy(string methodName, string expectedPolicy)
    {
        var method = typeof(OpenIddictManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == methodName);
        var policy = method.GetCustomAttributes<AuthorizeAttribute>()
            .Single(attribute => !string.IsNullOrWhiteSpace(attribute.Policy));

        Assert.Equal(expectedPolicy, policy.Policy);
    }

    [Theory]
    [InlineData(nameof(OpenIddictGrantManagementController.GetAuthorizationListAsync), OpenIddictManagementPolicies.Authorizations.Read)]
    [InlineData(nameof(OpenIddictGrantManagementController.RevokeAuthorizationAsync), OpenIddictManagementPolicies.Authorizations.Revoke)]
    [InlineData(nameof(OpenIddictGrantManagementController.GetTokenListAsync), OpenIddictManagementPolicies.Tokens.Read)]
    [InlineData(nameof(OpenIddictGrantManagementController.RevokeTokenAsync), OpenIddictManagementPolicies.Tokens.Revoke)]
    public void IssuedGrantActions_Require_Exact_HostPolicy(string methodName, string expectedPolicy)
    {
        var method = typeof(OpenIddictGrantManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == methodName);
        var policy = method.GetCustomAttributes<AuthorizeAttribute>()
            .Single(attribute => !string.IsNullOrWhiteSpace(attribute.Policy));

        Assert.Equal(expectedPolicy, policy.Policy);
    }

    [Theory]
    [InlineData(nameof(OpenIddictGrantManagementController.RevokeAuthorizationAsync))]
    [InlineData(nameof(OpenIddictGrantManagementController.RevokeTokenAsync))]
    public void IssuedGrantRevocations_Are_Audited_Post_Actions(string methodName)
    {
        var method = typeof(OpenIddictGrantManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == methodName);

        Assert.Contains(method.GetCustomAttributes(), attribute => attribute.GetType().Name == "AuditedAttribute");
        var httpPost = Assert.Single(method.GetCustomAttributes<HttpPostAttribute>());
        Assert.EndsWith("/revoke", httpPost.Template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(OpenIddictGrantManagementController.GetAuthorizationListAsync))]
    [InlineData(nameof(OpenIddictGrantManagementController.GetTokenListAsync))]
    public void IssuedGrantMetadataReads_Are_Explicitly_Audited(string methodName)
    {
        var method = typeof(OpenIddictGrantManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == methodName);

        Assert.Contains(method.GetCustomAttributes(), attribute => attribute.GetType().Name == "AuditedAttribute");
        Assert.Single(method.GetCustomAttributes<HttpGetAttribute>());
    }

    [Fact]
    public void IssuedTokenModel_Does_Not_Expose_Secrets_Payloads_Or_ReferenceIds()
    {
        var properties = typeof(OpenIddictTokenModel).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Payload", properties);
        Assert.DoesNotContain("ReferenceId", properties);
        Assert.DoesNotContain("Token", properties);
        Assert.DoesNotContain("Value", properties);
        Assert.DoesNotContain("ClientSecret", properties);
    }

    [Fact]
    public void SubjectGrantRevocation_Requires_Both_Revoke_Permissions_And_Audit()
    {
        var method = typeof(OpenIddictGrantManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(OpenIddictGrantManagementController.RevokeSubjectGrantsAsync));
        var policies = method.GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .Where(policy => policy != null)
            .ToHashSet(StringComparer.Ordinal);

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            OpenIddictManagementPolicies.Authorizations.Revoke,
            OpenIddictManagementPolicies.Tokens.Revoke
        };
        Assert.True(expected.SetEquals(policies!));
        Assert.Contains(method.GetCustomAttributes(), attribute => attribute.GetType().Name == "AuditedAttribute");
        Assert.EndsWith(
            "revoke-by-subject",
            Assert.Single(method.GetCustomAttributes<HttpPostAttribute>()).Template,
            StringComparison.Ordinal);
    }

    private static void AssertReturnType<T>(string methodName)
    {
        var method = typeof(OpenIddictManagementController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == methodName);

        Assert.Equal(typeof(Task<ActionResult<T>>), method.ReturnType);
    }
}
