using System.Text.Json;
using Auth.Caching;
using Auth.EntityFrameworkCore;
using Auth.Managers;
using Auth.Models;
using Auth.TokenMetadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Tests;

public sealed class OpenIddictManagementManagerTests
{
    [Fact]
    public async Task Credential_lifecycle_is_reveal_once_retry_safe_and_invalidates_caches()
    {
        using var harness = ManagementHarness.Create();
        var created = await harness.Manager.CreateApplicationAsync(
            ConfidentialClient("console-integration"),
            actor: "operator-1",
            CancellationToken.None);
        var initialSecret = Assert.IsType<string>(created.ClientSecret);
        var application = await harness.ApplicationManager.FindByClientIdAsync("console-integration");
        Assert.NotNull(application);

        Assert.True(await harness.ApplicationManager.ValidateClientSecretAsync(application!, initialSecret));
        Assert.DoesNotContain(initialSecret, JsonSerializer.Serialize(harness.ApplicationCache.Current), StringComparison.Ordinal);
        Assert.Equal(1, harness.TokenMetadataInvalidation.ClientInvalidationCount);

        var rotated = await harness.Manager.RotateSecretAsync(
            "console-integration",
            created.Application.ConcurrencyToken,
            actor: "operator-2",
            CancellationToken.None);
        var rotatedSecret = Assert.IsType<string>(rotated.ClientSecret);

        Assert.False(await harness.ApplicationManager.ValidateClientSecretAsync(application, initialSecret));
        Assert.True(await harness.ApplicationManager.ValidateClientSecretAsync(application, rotatedSecret));
        Assert.True(rotated.Application.Credential.IsConfigured);
        Assert.Equal("operator-2", rotated.Application.Credential.LastRotatedByActor);
        Assert.Equal(2, harness.ApplicationCache.SetCount);
        Assert.Equal(2, harness.TokenMetadataInvalidation.ClientInvalidationCount);
        Assert.DoesNotContain(rotatedSecret, JsonSerializer.Serialize(harness.ApplicationCache.Current), StringComparison.Ordinal);

        var replay = await Assert.ThrowsAsync<OpenIddictManagementException>(() =>
            harness.Manager.RotateSecretAsync(
                "console-integration",
                created.Application.ConcurrencyToken,
                actor: "operator-2",
                CancellationToken.None));

        Assert.Equal(409, replay.StatusCode);
        Assert.Equal("concurrency_conflict", replay.Code);
        Assert.True(await harness.ApplicationManager.ValidateClientSecretAsync(application, rotatedSecret));
        Assert.Equal(2, harness.TokenMetadataInvalidation.ClientInvalidationCount);

        var revoked = await harness.Manager.RevokeSecretAsync(
            "console-integration",
            rotated.Application.ConcurrencyToken,
            actor: "operator-3",
            CancellationToken.None);

        Assert.False(await harness.ApplicationManager.ValidateClientSecretAsync(application, rotatedSecret));
        Assert.False(revoked.Credential.IsConfigured);
        Assert.NotNull(revoked.Credential.RevokedAtUtc);
        Assert.Equal("operator-3", revoked.Credential.RevokedByActor);
        Assert.Equal(3, harness.ApplicationCache.SetCount);
        Assert.Equal(3, harness.TokenMetadataInvalidation.ClientInvalidationCount);
        Assert.DoesNotContain(initialSecret, JsonSerializer.Serialize(harness.ApplicationCache.Current), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedSecret, JsonSerializer.Serialize(harness.ApplicationCache.Current), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_cannot_change_client_type_and_delete_refuses_current_client()
    {
        using var harness = ManagementHarness.Create();
        var created = await harness.Manager.CreateApplicationAsync(
            ConfidentialClient("current-console-client"),
            actor: "operator-1",
            CancellationToken.None);

        var typeChange = new OpenIddictApplicationUpdateInput
        {
            ClientId = created.Application.ClientId,
            DisplayName = created.Application.DisplayName,
            ClientType = ClientTypes.Public,
            ConsentType = created.Application.ConsentType,
            ConcurrencyToken = created.Application.ConcurrencyToken,
            Permissions = ToInput(created.Application.Permissions),
            RedirectUris = [.. created.Application.RedirectUris],
            PostLogoutRedirectUris = [.. created.Application.PostLogoutRedirectUris]
        };

        var typeChangeError = await Assert.ThrowsAsync<OpenIddictManagementException>(() =>
            harness.Manager.UpdateApplicationAsync(
                created.Application.ClientId,
                typeChange,
                actor: "operator-1",
                CancellationToken.None));

        Assert.Equal(409, typeChangeError.StatusCode);
        Assert.Equal("client_type_immutable", typeChangeError.Code);

        var selfLockoutError = await Assert.ThrowsAsync<OpenIddictManagementException>(() =>
            harness.Manager.DeleteApplicationAsync(
                created.Application.ClientId,
                currentRequestClientId: created.Application.ClientId,
                actor: "operator-1",
                CancellationToken.None));

        Assert.Equal(409, selfLockoutError.StatusCode);
        Assert.Equal("self_lockout", selfLockoutError.Code);
        Assert.NotNull(await harness.ApplicationManager.FindByClientIdAsync(created.Application.ClientId));
    }

    [Fact]
    public async Task Application_list_pages_searches_redirects_and_scopes_and_surfaces_public_pkce()
    {
        using var harness = ManagementHarness.Create();
        var publicClient = await harness.Manager.CreateApplicationAsync(
            PublicClient("browser-console", "https://app.example.test/auth/callback", "workflows"),
            actor: "operator-1",
            CancellationToken.None);
        await harness.Manager.CreateApplicationAsync(
            ConfidentialClient("service-console"),
            actor: "operator-1",
            CancellationToken.None);

        var redirectSearch = await harness.Manager.GetApplicationsAsync(
            "app.example.test",
            clientType: ClientTypes.Public,
            configurationManaged: false,
            skipCount: 0,
            maxResultCount: 10,
            CancellationToken.None);
        var scopeSearch = await harness.Manager.GetApplicationsAsync(
            "workflows",
            clientType: null,
            configurationManaged: null,
            skipCount: 0,
            maxResultCount: 1,
            CancellationToken.None);

        var redirectMatch = Assert.Single(redirectSearch.Items);
        Assert.Equal(publicClient.Application.ClientId, redirectMatch.ClientId);
        Assert.True(redirectMatch.RequiresPkce);
        Assert.Equal(1, redirectSearch.TotalCount);
        Assert.Equal(1, scopeSearch.TotalCount);
        Assert.False(scopeSearch.HasMore);
    }

    [Fact]
    public async Task Configuration_managed_clients_are_rejected_before_mutation()
    {
        using var harness = ManagementHarness.Create();

        var error = await Assert.ThrowsAsync<OpenIddictManagementException>(() =>
            harness.Manager.RotateSecretAsync(
                "tenantwright-gateway",
                expectedConcurrencyToken: "stale",
                actor: "operator-1",
                CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("configuration_managed", error.Code);
        Assert.NotNull(error.Remediation);
        Assert.Equal(0, harness.ApplicationCache.SetCount);
        Assert.Equal(0, harness.TokenMetadataInvalidation.ClientInvalidationCount);
    }

    [Fact]
    public async Task Gateway_client_rejects_every_console_mutation_path()
    {
        using var harness = ManagementHarness.Create();
        const string clientId = "tenantwright-gateway";
        var update = new OpenIddictApplicationUpdateInput { ClientId = clientId };
        Func<Task>[] mutations =
        [
            async () => await harness.Manager.UpdateApplicationAsync(
                clientId,
                update,
                actor: "operator-1",
                CancellationToken.None),
            async () => await harness.Manager.RotateSecretAsync(
                clientId,
                expectedConcurrencyToken: "stale",
                actor: "operator-1",
                CancellationToken.None),
            async () => await harness.Manager.RevokeSecretAsync(
                clientId,
                expectedConcurrencyToken: "stale",
                actor: "operator-1",
                CancellationToken.None),
            async () => await harness.Manager.DeleteApplicationAsync(
                clientId,
                currentRequestClientId: null,
                actor: "operator-1",
                CancellationToken.None)
        ];

        foreach (var mutation in mutations)
        {
            var error = await Assert.ThrowsAsync<OpenIddictManagementException>(mutation);
            Assert.Equal(409, error.StatusCode);
            Assert.Equal("configuration_managed", error.Code);
        }

        Assert.Equal(0, harness.ApplicationCache.SetCount);
        Assert.Equal(0, harness.TokenMetadataInvalidation.ClientInvalidationCount);
    }

    private static OpenIddictApplicationCreateInput PublicClient(
        string clientId,
        string redirectUri,
        string scope) => new()
        {
            ClientId = clientId,
            DisplayName = "Browser integration",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            Permissions = new OpenIddictApplicationPermissionInput
            {
                GrantTypes = [GrantTypes.AuthorizationCode],
                Endpoints = ["authorization", "token"],
                Scopes = [scope],
                ResponseTypes = [ResponseTypes.Code],
                AdditionalPermissions = []
            },
            RedirectUris = [redirectUri],
            PostLogoutRedirectUris = []
        };

    private static OpenIddictApplicationCreateInput ConfidentialClient(string clientId) => new()
    {
        ClientId = clientId,
        DisplayName = "Console integration",
        ClientType = ClientTypes.Confidential,
        ConsentType = ConsentTypes.Implicit,
        Permissions = new OpenIddictApplicationPermissionInput
        {
            GrantTypes = [GrantTypes.ClientCredentials],
            Endpoints = ["token"],
            Scopes = [],
            ResponseTypes = [],
            AdditionalPermissions = []
        },
        RedirectUris = [],
        PostLogoutRedirectUris = []
    };

    private static OpenIddictApplicationPermissionInput ToInput(OpenIddictApplicationPermissionModel permissions) => new()
    {
        GrantTypes = [.. permissions.GrantTypes],
        Endpoints = [.. permissions.Endpoints],
        Scopes = [.. permissions.Scopes],
        ResponseTypes = [.. permissions.ResponseTypes],
        AdditionalPermissions = [.. permissions.AdditionalPermissions]
    };

    private sealed class ManagementHarness : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly IServiceScope _scope;

        private ManagementHarness(ServiceProvider serviceProvider, IServiceScope scope)
        {
            _serviceProvider = serviceProvider;
            _scope = scope;
            var services = scope.ServiceProvider;
            ApplicationManager = services.GetRequiredService<IOpenIddictApplicationManager>();
            ApplicationCache = new RecordingApplicationCache();
            TokenMetadataInvalidation = new RecordingTokenMetadataInvalidationService();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenIddict:Issuer"] = "https://auth.example.test"
                })
                .Build();

            Manager = new OpenIddictManagementManager(
                ApplicationManager,
                services.GetRequiredService<IOpenIddictScopeManager>(),
                services.GetRequiredService<IOpenIddictAuthorizationManager>(),
                services.GetRequiredService<IOpenIddictTokenManager>(),
                ApplicationCache,
                new RecordingScopeCache(),
                TokenMetadataInvalidation,
                new OpenIddictManagementValidator(configuration),
                configuration,
                NullLogger<OpenIddictManagementManager>.Instance);
        }

        public OpenIddictManagementManager Manager { get; }
        public IOpenIddictApplicationManager ApplicationManager { get; }
        public RecordingApplicationCache ApplicationCache { get; }
        public RecordingTokenMetadataInvalidationService TokenMetadataInvalidation { get; }

        public static ManagementHarness Create()
        {
            var services = new ServiceCollection();
            services.AddDbContext<AuthDbContext>(options =>
            {
                options.UseInMemoryDatabase($"auth-management-{Guid.NewGuid():N}");
                options.UseOpenIddict();
            });
            services.AddOpenIddict()
                .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AuthDbContext>());

            var serviceProvider = services.BuildServiceProvider();
            return new ManagementHarness(serviceProvider, serviceProvider.CreateScope());
        }

        public void Dispose()
        {
            _scope.Dispose();
            _serviceProvider.Dispose();
        }
    }

    private sealed class RecordingApplicationCache : IOpenIddictApplicationCache
    {
        public OpenIddictApplicationModel? Current { get; private set; }
        public int SetCount { get; private set; }

        public Task<OpenIddictApplicationModel?> GetByClientIdAsync(string clientId, CancellationToken token = default)
            => Task.FromResult(Current?.ClientId == clientId ? Current : null);

        public Task SetAsync(OpenIddictApplicationModel application, CancellationToken token = default)
        {
            Current = application;
            SetCount++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string clientId, CancellationToken token = default)
        {
            if (Current?.ClientId == clientId)
            {
                Current = null;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScopeCache : IOpenIddictScopeCache
    {
        public Task<OpenIddictScopeModel?> GetByNameAsync(string name, CancellationToken token = default)
            => Task.FromResult<OpenIddictScopeModel?>(null);

        public Task SetAsync(OpenIddictScopeModel scope, CancellationToken token = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string name, CancellationToken token = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingTokenMetadataInvalidationService : ITokenMetadataInvalidationService
    {
        public int ClientInvalidationCount { get; private set; }

        public Task InvalidateClientAsync(string clientId, CancellationToken cancellationToken = default)
        {
            ClientInvalidationCount++;
            return Task.CompletedTask;
        }

        public Task InvalidateScopeAsync(string scopeName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InvalidateAllAsync(string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
