using System.Reflection;
using Auth.Data;
using Auth.Managers;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using Xunit;

namespace Auth.Tests;

public sealed class OpenIddictDataSeedContributorTests
{
    private static readonly string ScopePrefix = OpenIddictConstants.Permissions.Prefixes.Scope;

    [Theory]
    [InlineData("identity", true)]
    [InlineData("workflows", true)]
    [InlineData("auditlogs", true)]
    [InlineData("identity-internal", false)]
    [InlineData("tenant-internal", false)]
    [InlineData("permissions-internal", false)]
    public async Task TenantWright_gateway_descriptor_grants_public_service_scopes_but_never_internal_scopes(
        string scope, bool allowed)
    {
        var applicationManager = DispatchProxy.Create<IOpenIddictApplicationManager, RecordingApplicationManager>();
        var recordingManager = (RecordingApplicationManager)(object)applicationManager;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenIddict:Clients:TenantWrightGateway:ClientId"] = "tenantwright-gateway",
                ["OpenIddict:Clients:TenantWrightGateway:RedirectUri"] = "https://gateway.example.test/auth/callback",
                ["OpenIddict:Clients:TenantWrightGateway:PostLogoutRedirectUri"] = "https://gateway.example.test/auth/logout-callback"
            })
            .Build();
        var contributor = new OpenIddictDataSeedContributor(
            configuration,
            applicationManager,
            null!,
            new OpenIddictManagementValidator(configuration));
        var method = typeof(OpenIddictDataSeedContributor).GetMethod(
            "SeedTenantWrightGatewayClientAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        await Assert.IsAssignableFrom<Task>(method!.Invoke(contributor, null));

        var descriptor = Assert.Single(recordingManager.CreatedDescriptors);
        Assert.Equal(allowed, descriptor.Permissions.Contains(ScopePrefix + scope));
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, descriptor.ClientType);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
        AssertGatewayEndpoints(descriptor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantWright_gateway_reseed_updates_existing_client_and_removes_stale_scope_grants(bool confidential)
    {
        var applications = DispatchProxy.Create<IOpenIddictApplicationManager, RecordingApplicationManager>();
        var manager = (RecordingApplicationManager)(object)applications;
        var existing = new object();
        manager.Application = existing;
        manager.ExistingDescriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = "tenantwright-gateway",
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            ClientSecret = "obsolete-test-secret"
        };
        manager.ExistingDescriptor.Permissions.UnionWith(
        [
            ScopePrefix + "identity-internal",
            ScopePrefix + "tenant-internal",
            ScopePrefix + "stale-scope"
        ]);
        manager.ExistingDescriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        manager.ExistingDescriptor.RedirectUris.Add(new Uri("https://obsolete.example.test/auth/callback"));
        manager.ExistingDescriptor.PostLogoutRedirectUris.Add(new Uri("https://obsolete.example.test/auth/logout-callback"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenIddict:Clients:TenantWrightGateway:ClientId"] = "tenantwright-gateway",
            ["OpenIddict:Clients:TenantWrightGateway:RedirectUri"] = "https://gateway.example.test/auth/callback",
            ["OpenIddict:Clients:TenantWrightGateway:PostLogoutRedirectUri"] = "https://gateway.example.test/auth/logout-callback",
            ["OpenIddict:Clients:TenantWrightGateway:Secret"] = confidential ? "configured-test-secret" : null,
            ["OpenIddict:AllowPasswordFlow"] = "false"
        }).Build();
        var contributor = new OpenIddictDataSeedContributor(configuration, applications, null!, new OpenIddictManagementValidator(configuration));
        var method = typeof(OpenIddictDataSeedContributor).GetMethod("SeedTenantWrightGatewayClientAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method.Invoke(contributor, null));
        var updated = Assert.Single(manager.UpdatedDescriptors);
        Assert.Same(existing, manager.UpdatedApplication);
        Assert.Empty(manager.CreatedDescriptors);
        Assert.Equal(1, manager.PopulateCount);
        Assert.Equal(confidential ? OpenIddictConstants.ClientTypes.Confidential : OpenIddictConstants.ClientTypes.Public, updated.ClientType);
        Assert.Equal(confidential ? "configured-test-secret" : null, updated.ClientSecret);
        Assert.Equal(!confidential, updated.Requirements.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange));
        Assert.DoesNotContain(ScopePrefix + "identity-internal", updated.Permissions);
        Assert.DoesNotContain(ScopePrefix + "tenant-internal", updated.Permissions);
        Assert.DoesNotContain(ScopePrefix + "stale-scope", updated.Permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.GrantTypes.Password, updated.Permissions);
        AssertGatewayEndpoints(updated);
        Assert.Equal("https://gateway.example.test/auth/callback", Assert.Single(updated.RedirectUris).ToString());
        Assert.Equal("https://gateway.example.test/auth/logout-callback", Assert.Single(updated.PostLogoutRedirectUris).ToString());
        Assert.Equal(
            new[]
            {
                "auditlogs", "email", "identity", "notifications", "offline_access", "openid", "permissions",
                "profile", "roles", "tenant", "workflows"
            },
            ScopesOf(updated));
        await Assert.IsAssignableFrom<Task>(method.Invoke(contributor, null));
        Assert.Equal(2, manager.PopulateCount);
        Assert.Equal(2, manager.UpdatedDescriptors.Count);
        Assert.Equal(updated.Permissions.Order(StringComparer.Ordinal), manager.UpdatedDescriptors[1].Permissions.Order(StringComparer.Ordinal));
        Assert.Empty(manager.CreatedDescriptors);
        Assert.Equal(0, manager.DeleteCount);
    }

    private static void AssertGatewayEndpoints(OpenIddictApplicationDescriptor descriptor)
    {
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Authorization, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Token, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.EndSession, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Revocation, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.ResponseTypes.Code, descriptor.Permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials, descriptor.Permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.GrantTypes.TokenExchange, descriptor.Permissions);
    }

    [Fact]
    public void ReadConfiguredUris_should_reject_postman_fallback_when_fallbacks_are_disabled()
    {
        var contributor = CreateContributor([]);

        var ex = Assert.Throws<TargetInvocationException>(() =>
            InvokeReadConfiguredUris(
                contributor,
                "OpenIddict:Clients:TenantWrightGateway:RedirectUris",
                "OpenIddict:Clients:TenantWrightGateway:RedirectUri",
                "https://oauth.pstmn.io/v1/callback",
                allowFallback: false));

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Explicit redirect URI configuration is required outside Development.", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadConfiguredUris_should_allow_configured_redirect_uri_when_fallbacks_are_disabled()
    {
        var contributor = CreateContributor(new Dictionary<string, string?>
        {
            ["OpenIddict:Clients:TenantWrightGateway:RedirectUri"] = "https://gateway.example.test/auth/callback"
        });

        var values = InvokeReadConfiguredUris(
            contributor,
            "OpenIddict:Clients:TenantWrightGateway:RedirectUris",
            "OpenIddict:Clients:TenantWrightGateway:RedirectUri",
            "https://oauth.pstmn.io/v1/callback",
            allowFallback: false);

        Assert.Equal(["https://gateway.example.test/auth/callback"], values);
    }

    /// <summary>
    /// Every scope the seed contributor owns must also be listed as configuration-managed, and vice versa.
    /// </summary>
    /// <remarks>
    /// A seeded scope that the validator does not recognise can be created and edited through the management
    /// console, and the next seed run silently overwrites whatever an operator did. Worse, an operator can
    /// point a service scope at a different resource and change which audience tokens are minted for.
    ///
    /// This is asserted as an invariant over the seeding methods rather than as a list of names, because a
    /// hand-maintained list is exactly what drifts when the next service is onboarded.
    /// </remarks>
    [Fact]
    public async Task Every_seeded_scope_is_configuration_managed()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var validator = new OpenIddictManagementValidator(configuration);
        var scopeManager = DispatchProxy.Create<IOpenIddictScopeManager, RecordingScopeManager>();
        var recordingManager = (RecordingScopeManager)(object)scopeManager;
        var contributor = new OpenIddictDataSeedContributor(
            configuration,
            null!,
            scopeManager,
            validator);

        var seedingMethods = typeof(OpenIddictDataSeedContributor)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method =>
                method.Name.StartsWith("Seed", StringComparison.Ordinal) &&
                method.Name.EndsWith("Async", StringComparison.Ordinal) &&
                method.Name.Contains("Scope", StringComparison.Ordinal) &&
                method.GetParameters().Length == 0)
            .ToList();

        Assert.NotEmpty(seedingMethods);

        foreach (var method in seedingMethods)
        {
            await Assert.IsAssignableFrom<Task>(method.Invoke(contributor, null));
        }

        Assert.NotEmpty(recordingManager.CreatedScopeNames);
        Assert.Empty(recordingManager.CreatedScopeNames.Where(name => !validator.IsConfigurationManagedScope(name)));
        Assert.Equal(
            validator.ConfigurationManagedScopeNames.Order(StringComparer.Ordinal),
            recordingManager.CreatedScopeNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Each service scope must resolve exactly to the audiences its host validates.
    /// </summary>
    /// <remarks>
    /// A host calls <c>AddAudiences(...)</c> with its resource names. If a scope's resources drift, every request
    /// is rejected as an audience mismatch (or worse, a token is minted for another service's audience) — a
    /// failure that looks like broken authorization rather than broken configuration, so it is pinned here.
    /// </remarks>
    [Theory]
    [InlineData("SeedIdentityApiScopeAsync", "identity", "IdentityService", "identity")]
    [InlineData("SeedTenantApiScopeAsync", "tenant", "TenantService", "tenant")]
    [InlineData("SeedPermissionsApiScopeAsync", "permissions", "PermissionsService", "permissions")]
    [InlineData("SeedNotificationsApiScopeAsync", "notifications", "NotificationsService", "notifications")]
    [InlineData("SeedAuditLogsApiScopeAsync", "auditlogs", "AuditLogsService", "auditlogs")]
    [InlineData("SeedWorkflowsApiScopeAsync", "workflows", "WorkflowsService", "workflows")]
    [InlineData("SeedIdentityInternalScopeAsync", "identity-internal", "IdentityService", "identity")]
    [InlineData("SeedNotificationsInternalScopeAsync", "notifications-internal", "NotificationsService", "notifications")]
    [InlineData("SeedTenantInternalScopeAsync", "tenant-internal", "TenantService", "tenant")]
    [InlineData("SeedPermissionsInternalScopeAsync", "permissions-internal", "PermissionsService", "permissions")]
    [InlineData("SeedFeaturesCheckScopeAsync", "features.check", "PermissionsService", "permissions")]
    [InlineData("SeedCapabilityMetadataScopeAsync", "capabilities.read", "PermissionsService", "permissions")]
    [InlineData("SeedEntitlementProvisioningScopeAsync", "entitlements.provision", "PermissionsService", "permissions")]
    [InlineData("SeedTenantServiceabilityScopeAsync", "tenant-serviceability.read", "TenantService", "tenant")]
    public async Task Service_scope_targets_only_its_owning_audience(
        string methodName, string scopeName, string serviceResource, string audienceResource)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var scopeManager = DispatchProxy.Create<IOpenIddictScopeManager, RecordingScopeManager>();
        var recordingManager = (RecordingScopeManager)(object)scopeManager;
        var contributor = new OpenIddictDataSeedContributor(
            configuration,
            null!,
            scopeManager,
            new OpenIddictManagementValidator(configuration));
        var method = typeof(OpenIddictDataSeedContributor).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        await Assert.IsAssignableFrom<Task>(method!.Invoke(contributor, null));

        var descriptor = Assert.Single(recordingManager.CreatedDescriptors);
        Assert.Equal(scopeName, descriptor.Name);
        Assert.Equal(
            new[] { serviceResource, audienceResource }.Order(StringComparer.Ordinal),
            descriptor.Resources.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Auth_internal_client_is_confidential_and_granted_only_its_internal_scopes()
    {
        var descriptor = await SeedSingleClientAsync(
            "SeedInternalClientsAsync",
            new Dictionary<string, string?>
            {
                ["OpenIddict:Clients:Internal:ClientId"] = "auth-internal",
                ["OpenIddict:Clients:Internal:Secret"] = "internal-test-secret"
            });

        Assert.Equal("auth-internal", descriptor.ClientId);
        Assert.Equal(OpenIddictConstants.ClientTypes.Confidential, descriptor.ClientType);
        Assert.Equal(
            new[]
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                ScopePrefix + "features.check",
                ScopePrefix + "identity-internal",
                ScopePrefix + "notifications-internal",
                ScopePrefix + "tenant-internal"
            },
            descriptor.Permissions.Order(StringComparer.Ordinal));
        Assert.Empty(descriptor.RedirectUris);
        Assert.Empty(descriptor.PostLogoutRedirectUris);
    }

    [Fact]
    public async Task Notifications_internal_client_is_granted_only_the_identity_internal_scope()
    {
        var descriptor = await SeedSingleClientAsync(
            "SeedNotificationsServiceClientAsync",
            new Dictionary<string, string?>
            {
                ["OpenIddict:Clients:NotificationsInternal:ClientId"] = "tenantwright-notifications-internal",
                ["OpenIddict:Clients:NotificationsInternal:Secret"] = "notifications-test-secret"
            });

        Assert.Equal("tenantwright-notifications-internal", descriptor.ClientId);
        Assert.Equal(OpenIddictConstants.ClientTypes.Confidential, descriptor.ClientType);
        Assert.Equal(
            new[]
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                ScopePrefix + "identity-internal"
            },
            descriptor.Permissions.Order(StringComparer.Ordinal));
        Assert.Empty(descriptor.RedirectUris);
        Assert.Empty(descriptor.PostLogoutRedirectUris);
    }

    [Theory]
    [InlineData("SeedInternalClientsAsync", "OpenIddict:Clients:Internal:Secret")]
    [InlineData("SeedNotificationsServiceClientAsync", "OpenIddict:Clients:NotificationsInternal:Secret")]
    public async Task Internal_client_requires_a_secret_outside_development(string methodName, string secretKey)
    {
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        try
        {
            var applicationManager = DispatchProxy.Create<IOpenIddictApplicationManager, RecordingApplicationManager>();
            var recordingManager = (RecordingApplicationManager)(object)applicationManager;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
            var contributor = new OpenIddictDataSeedContributor(
                configuration,
                applicationManager,
                null!,
                new OpenIddictManagementValidator(configuration));
            var method = typeof(OpenIddictDataSeedContributor).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Assert.IsAssignableFrom<Task>(method!.Invoke(contributor, null)));

            Assert.Contains(secretKey, exception.Message, StringComparison.Ordinal);
            Assert.Empty(recordingManager.CreatedDescriptors);
            Assert.Empty(recordingManager.UpdatedDescriptors);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
        }
    }

    private static async Task<OpenIddictApplicationDescriptor> SeedSingleClientAsync(
        string methodName,
        Dictionary<string, string?> configurationValues)
    {
        var applicationManager = DispatchProxy.Create<IOpenIddictApplicationManager, RecordingApplicationManager>();
        var recordingManager = (RecordingApplicationManager)(object)applicationManager;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();
        var contributor = new OpenIddictDataSeedContributor(
            configuration,
            applicationManager,
            null!,
            new OpenIddictManagementValidator(configuration));
        var method = typeof(OpenIddictDataSeedContributor).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        await Assert.IsAssignableFrom<Task>(method!.Invoke(contributor, null));

        return Assert.Single(recordingManager.CreatedDescriptors);
    }

    private static string[] ScopesOf(OpenIddictApplicationDescriptor descriptor) =>
        descriptor.Permissions
            .Where(permission => permission.StartsWith(ScopePrefix, StringComparison.Ordinal))
            .Select(permission => permission[ScopePrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static OpenIddictDataSeedContributor CreateContributor(Dictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        return new OpenIddictDataSeedContributor(
            configuration,
            null!,
            null!,
            new OpenIddictManagementValidator(configuration));
    }

    private static IReadOnlyList<string> InvokeReadConfiguredUris(
        OpenIddictDataSeedContributor contributor,
        string collectionKey,
        string singleKey,
        string fallback,
        bool allowFallback)
    {
        var method = typeof(OpenIddictDataSeedContributor).GetMethod(
            "ReadConfiguredUris",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(
            contributor,
            [collectionKey, singleKey, fallback, allowFallback]));
    }

#pragma warning disable CA1852 // DispatchProxy creates a runtime subclass of the proxy base.
#pragma warning disable CA2012 // DispatchProxy requires boxed ValueTask return values for interface methods.
    /// <summary>
    /// Records scope creation. Every lookup reports "missing" so each seeding method takes its create path.
    /// </summary>
    private class RecordingScopeManager : DispatchProxy
    {
        private readonly List<OpenIddictScopeDescriptor> _createdDescriptors = [];

        public List<OpenIddictScopeDescriptor> CreatedDescriptors => _createdDescriptors;

        public IReadOnlyList<string> CreatedScopeNames =>
            _createdDescriptors.Select(descriptor => descriptor.Name ?? string.Empty).ToList();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IOpenIddictScopeManager.FindByNameAsync) => (object)new ValueTask<object?>((object?)null),
                nameof(IOpenIddictScopeManager.CreateAsync) => RecordCreate(args),
                _ => throw new NotSupportedException($"Unexpected scope manager call: {targetMethod?.Name}")
            };
        }

        private ValueTask<object> RecordCreate(object?[]? args)
        {
            var descriptor = Assert.IsType<OpenIddictScopeDescriptor>(args?[0]);
            _createdDescriptors.Add(descriptor);
            return new ValueTask<object>(new object());
        }
    }

    private class RecordingApplicationManager : DispatchProxy
    {
        private readonly List<OpenIddictApplicationDescriptor> _createdDescriptors = [];
        private readonly List<OpenIddictApplicationDescriptor> _updatedDescriptors = [];

        public object? Application { get; set; }
        public OpenIddictApplicationDescriptor? ExistingDescriptor { get; set; }
        public object? UpdatedApplication { get; private set; }
        public int PopulateCount { get; private set; }
        public object? DeletedApplication { get; private set; }
        public int DeleteCount { get; private set; }
        public IReadOnlyList<OpenIddictApplicationDescriptor> CreatedDescriptors => _createdDescriptors;
        public List<OpenIddictApplicationDescriptor> UpdatedDescriptors => _updatedDescriptors;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IOpenIddictApplicationManager.FindByClientIdAsync) =>
                    new ValueTask<object?>(Application),
                nameof(IOpenIddictApplicationManager.CreateAsync) => RecordCreate(args),
                nameof(IOpenIddictApplicationManager.PopulateAsync) => Populate(args),
                nameof(IOpenIddictApplicationManager.UpdateAsync) => RecordUpdate(args),
                nameof(IOpenIddictApplicationManager.DeleteAsync) => RecordDelete(args),
                _ => throw new NotSupportedException($"Unexpected application manager call: {targetMethod?.Name}")
            };
        }

        private ValueTask<object> RecordCreate(object?[]? args)
        {
            _createdDescriptors.Add(Assert.IsType<OpenIddictApplicationDescriptor>(args?[0]));
            return new ValueTask<object>(new object());
        }

        private ValueTask Populate(object?[]? args)
        {
            var descriptor = Assert.IsType<OpenIddictApplicationDescriptor>(args?[0]);
            Assert.Same(Application, args?[1]); Assert.NotNull(ExistingDescriptor); PopulateCount++;
            descriptor.ClientId = ExistingDescriptor.ClientId; descriptor.ClientType = ExistingDescriptor.ClientType;
            descriptor.ClientSecret = ExistingDescriptor.ClientSecret;
            descriptor.Permissions.UnionWith(ExistingDescriptor.Permissions);
            descriptor.Requirements.UnionWith(ExistingDescriptor.Requirements);
            descriptor.RedirectUris.UnionWith(ExistingDescriptor.RedirectUris);
            descriptor.PostLogoutRedirectUris.UnionWith(ExistingDescriptor.PostLogoutRedirectUris);
            return ValueTask.CompletedTask;
        }

        private ValueTask RecordUpdate(object?[]? args)
        {
            UpdatedApplication = args?[0]; Assert.Same(Application, UpdatedApplication);
            var descriptor = Assert.IsType<OpenIddictApplicationDescriptor>(args?[1]);
            _updatedDescriptors.Add(descriptor); ExistingDescriptor = descriptor;
            return ValueTask.CompletedTask;
        }

        private ValueTask RecordDelete(object?[]? args)
        {
            DeletedApplication = args?[0];
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }
#pragma warning restore CA2012
#pragma warning restore CA1852
}
