using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Permissions;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.PermissionEvaluation;
using Permissions.Application.PermissionManagement;
using Permissions.Controllers.PermissionEvaluation;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;
using Xunit;

namespace Permissions.Tests;

public sealed class SubjectPermissionAuthorizationTests
{
    private const string ApprovedClient = "tenantwright-sample-internal";

    [Fact]
    public async Task Internal_policy_requires_the_exact_scope_enabled_allow_listed_service_identity_and_matching_subject()
    {
        var enabled = BuildAuthorizationService(enabled: true, allowedClientIds: ApprovedClient);
        var approved = ServicePrincipal(ApprovedClient, ApprovedClient, "openid permissions-internal profile");

        Assert.True((await enabled.AuthorizeAsync(
            approved,
            resource: null,
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)).Succeeded);
        Assert.False((await enabled.AuthorizeAsync(
            ServicePrincipal(ApprovedClient, Guid.CreateVersion7().ToString("D"), "permissions-internal"),
            resource: null,
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)).Succeeded);
        Assert.False((await enabled.AuthorizeAsync(
            ServicePrincipal("tenantwright-unapproved", "tenantwright-unapproved", "permissions-internal"),
            resource: null,
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)).Succeeded);
        Assert.False((await enabled.AuthorizeAsync(
            ServicePrincipal(ApprovedClient, ApprovedClient, "permissions-internals"),
            resource: null,
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)).Succeeded);

        var disabled = BuildAuthorizationService(enabled: false, allowedClientIds: ApprovedClient);
        Assert.False((await disabled.AuthorizeAsync(
            approved,
            resource: null,
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)).Succeeded);
    }

    [Fact]
    public async Task Internal_policy_fails_closed_when_enabled_with_an_empty_allow_list()
    {
        var enabled = BuildAuthorizationService(enabled: true, allowedClientIds: string.Empty);

        Assert.False((await enabled.AuthorizeAsync(
            ServicePrincipal(ApprovedClient, ApprovedClient, PermissionsScopes.SubjectPermissionAuthorizationsInternal),
            resource: null,
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal)).Succeeded);
    }

    [Fact]
    public void Subject_authorization_controller_is_an_integration_route_behind_the_internal_policy()
    {
        var controller = typeof(SubjectPermissionAuthorizationsController);
        Assert.Single(controller.GetCustomAttributes(typeof(IntegrationServiceAttribute), inherit: true));
        Assert.Equal(
            "api/permissions/internal/subject-permission-authorizations",
            Assert.Single(controller.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>()).Template);
        Assert.Equal(
            PermissionsHostPolicies.SubjectPermissionAuthorizationInternal,
            Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>()).Policy);

        var method = controller.GetMethod(nameof(SubjectPermissionAuthorizationsController.AuthorizeCurrentSubjectAsync));
        Assert.NotNull(method);
        Assert.Null(Assert.Single(method!.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Cast<HttpPostAttribute>()).Template);
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Current_subject_evaluator_denies_ineligible_identity_accounts_without_evaluating_stale_role_claims(
        bool isActive,
        bool isLockedOut)
    {
        var service = new CurrentSubjectPermissionAuthorizationAppService(
            new StaticIdentityTargetValidator(isActive, isLockedOut),
            new PermissionAuthorizationEvaluator(null!, null!, null!, null!, null!, null!));

        var result = await service.AuthorizeCurrentSubjectAsync(new SubjectPermissionAuthorizationRequestDto
        {
            TenantId = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            Permissions = [IdentityPermissions.Users.Export]
        });

        Assert.False(result.IsAuthorized);
        Assert.Null(result.SnapshotHash);
    }

    private static IAuthorizationService BuildAuthorizationService(bool enabled, string allowedClientIds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthServer:Authority"] = "https://auth.test/",
                ["ConnectionStrings:Default"] = "Host=database.test;Database=permissions;Username=permissions;Password=test-only",
                ["Messaging:Provider"] = "None",
                ["PermissionsInternal:SubjectPermissionAuthorization:Enabled"] = enabled.ToString(),
                ["PermissionsInternal:SubjectPermissionAuthorization:AllowedClientIds"] = allowedClientIds,
            })
            .Build();
        var services = new ServiceCollection();
        new PermissionsHostModule().ConfigureServices(new Shared.Modularity.Context.ServiceConfigurationContext(services, configuration));
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal ServicePrincipal(string clientId, string subject, string scope) =>
        new(new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.ClientId, clientId),
                new Claim(OpenIddictConstants.Claims.Subject, subject),
                new Claim(OpenIddictConstants.Claims.Scope, scope)
            ],
            authenticationType: "Test"));

    private sealed class StaticIdentityTargetValidator(bool isActive, bool isLockedOut) : IIdentityPermissionTargetValidator
    {
        public Task<IdentityRolePermissionTarget> GetRoleAsync(
            Guid roleId,
            Guid? tenantId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IdentityUserPermissionTarget> GetUserAsync(
            Guid userId,
            Guid? tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new IdentityUserPermissionTarget(
                userId,
                "clinician",
                "Clinician",
                isActive,
                isLockedOut,
                [Guid.CreateVersion7()]));

        public Task<IReadOnlyList<IdentityResolvedPermissionTarget>> ResolveAsync(
            IReadOnlyCollection<IdentityPermissionTargetReference> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
