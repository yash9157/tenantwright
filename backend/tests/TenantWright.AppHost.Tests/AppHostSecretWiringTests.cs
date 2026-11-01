using System.Text.RegularExpressions;
using Xunit;

namespace TenantWright.AppHost.Tests;

public sealed class AppHostSecretWiringTests
{
    private static readonly string[] ServiceResourceNames =
        ["auth", "identity", "tenant", "permissions", "auditlogs", "notifications", "workflows"];

    [Fact]
    public void AppHost_should_route_local_secret_parameters_to_their_owning_services()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");

        Assert.Contains(
            "LocalSecrets.AddClientSecret(builder, \"internal-client-secret\")",
            appHostSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "LocalSecrets.AddClientSecret(builder, \"notifications-internal-client-secret\")",
            appHostSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "LocalSecrets.AddAes256Key(builder, \"notifications-provider-protection-key\")",
            appHostSource,
            StringComparison.Ordinal);

        var authSection = GetSection(appHostSource, "var auth =", "var identity =");
        Assert.Contains(
            ".WithEnvironment(\"OpenIddict__Clients__Internal__Secret\", internalClientSecret)",
            authSection,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"OpenIddict__Clients__NotificationsInternal__Secret\", notificationsInternalClientSecret)",
            authSection,
            StringComparison.Ordinal);
        // The provider-secret protection key belongs to Notifications only; Auth must never receive it.
        Assert.DoesNotContain("notificationsProviderProtectionKey", authSection, StringComparison.Ordinal);
        // The public-registration hash key belongs to Auth only.
        Assert.Contains("publicRegistrationHashKey", authSection, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(appHostSource, "publicRegistrationHashKey"));

        var permissionsSection = GetSection(appHostSource, "var permissions =", "var auditLogs =");
        Assert.Contains(
            ".WithEnvironment(\"IdentityPermissionTargetValidation__ClientSecret\", internalClientSecret)",
            permissionsSection,
            StringComparison.Ordinal);
        Assert.DoesNotContain("notificationsProviderProtectionKey", permissionsSection, StringComparison.Ordinal);

        var notificationsSection = GetSection(appHostSource, "var notifications =", "var workflows =");
        Assert.Contains(
            ".WithEnvironment(\"OpenIddict__Clients__NotificationsInternal__Secret\", notificationsInternalClientSecret)",
            notificationsSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Notifications__ProviderSecrets__Protection__Keys__notifications-provider-secrets-v1\"",
            notificationsSection,
            StringComparison.Ordinal);
        Assert.Contains("notificationsProviderProtectionKey", notificationsSection, StringComparison.Ordinal);
    }

    /// <summary>
    /// Internal and gateway traffic uses HTTP only.
    /// </summary>
    /// <remarks>
    /// TLS terminates at the Auth and gateway boundaries and the API gateway routes every service
    /// over HTTP. No internal destination currently targets an HTTPS endpoint; adding one must be a
    /// deliberate, reviewed exception rather than a silent expansion.
    /// </remarks>
    [Fact]
    public void AppHost_should_route_internal_and_gateway_traffic_over_http_only()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");
        var extensionsSource = RepositoryFiles.ReadAppHost("TenantWrightServiceExtensions.cs");

        foreach (var source in new[] { appHostSource, extensionsSource })
        {
            Assert.Equal(0, Regex.Count(source, "GetEndpoint\\(\\\"https\\\"\\)"));
            Assert.DoesNotContain("BaseAddress\", \"https://", source, StringComparison.Ordinal);
        }

        var gatewaySection = GetSection(appHostSource, "builder.AddTenantWrightService(\"apigateway\"", "builder.Build()");
        foreach (var resourceName in ServiceResourceNames)
        {
            Assert.Matches(
                $"\\.WithGatewayDestination\\(\"{resourceName}\", \\w+\\)",
                gatewaySection);
        }

        Assert.Equal(
            ServiceResourceNames.Length,
            Regex.Count(gatewaySection, "\\.WithGatewayDestination\\("));
        Assert.Matches(
            "\\$\"ReverseProxy__Clusters__\\{cluster\\}__Destinations__\\{cluster\\}_1__Address\",\\s+destination\\.GetEndpoint\\(\"http\"\\)",
            extensionsSource);
    }

    /// <summary>
    /// Backend services reached over HTTP must answer it: an HTTPS redirect makes HttpClient drop the Authorization
    /// header, so every internal call would fail with 401. Auth and the gateway are the TLS boundaries and keep it.
    /// </summary>
    [Fact]
    public void Backend_services_reached_over_http_do_not_redirect_to_https()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");
        var extensionsSource = RepositoryFiles.ReadAppHost("TenantWrightServiceExtensions.cs");

        var internalServices = ServiceResourceNames.Where(name => name != "auth").ToArray();
        foreach (var resourceName in internalServices)
        {
            Assert.Matches(
                $"AddTenantWrightService\\(\"{resourceName}\"[^\\n]*\\n\\s+\\.WithServiceInfrastructure\\([^\\n]*\\n\\s+\\.AsInternalService\\(\\)",
                appHostSource);
        }

        Assert.Equal(internalServices.Length, Regex.Count(appHostSource, "\\.AsInternalService\\(\\)"));
        Assert.DoesNotContain(".AsInternalService()", GetSection(appHostSource, "var auth =", "var identity ="), StringComparison.Ordinal);
        Assert.DoesNotContain(".AsInternalService()", GetSection(appHostSource, "builder.AddTenantWrightService(\"apigateway\"", "builder.Build()"), StringComparison.Ordinal);
        Assert.Contains(
            "service.WithEnvironment(\"Hosting__HttpsRedirection__Enabled\", \"false\")",
            GetSection(extensionsSource, "AsInternalService(", "TenantConnectionProtectionKeyId ="),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Dedicated tenant connection strings are encrypted by Tenant and decrypted by every service that resolves
    /// them, so every service must receive the same protection key and refuse plaintext values.
    /// </summary>
    [Fact]
    public void Every_service_receives_the_tenant_connection_string_protection_key()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");
        var extensionsSource = RepositoryFiles.ReadAppHost("TenantWrightServiceExtensions.cs");

        Assert.Equal(
            ServiceResourceNames.Length,
            Regex.Count(appHostSource, "\\.WithServiceInfrastructure\\(infrastructure, \"tenantwright_\\w+\", tenantConnectionProtectionKey\\)"));
        Assert.Equal(0, Regex.Count(appHostSource, "\\.WithServiceInfrastructure\\(infrastructure, \"tenantwright_\\w+\"\\)"));
        Assert.Contains("\"TenantConnectionStrings__Protection__CurrentKeyId\"", extensionsSource, StringComparison.Ordinal);
        Assert.Contains("\"TenantConnectionStrings__Protection__RequireProtectedValues\", \"true\"", extensionsSource, StringComparison.Ordinal);
        Assert.Contains("$\"TenantConnectionStrings__Protection__Keys__{TenantConnectionProtectionKeyId}\"", extensionsSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tenant learns the dedicatable services' shared databases as targets only, and each target names exactly the
    /// database that service is given; no credential reaches Tenant through them.
    /// </summary>
    [Fact]
    public void Tenant_receives_the_shared_database_targets_of_the_dedicatable_services_without_credentials()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");
        var extensionsSource = RepositoryFiles.ReadAppHost("TenantWrightServiceExtensions.cs");
        var tenantSection = GetSection(appHostSource, "var tenant =", "var permissions =");

        var targets = Regex
            .Matches(tenantSection, "\\(\"(?<service>AuditLogs|Notifications|Permissions)\", \"(?<database>tenantwright_\\w+)\"\\)")
            .ToDictionary(match => match.Groups["service"].Value, match => match.Groups["database"].Value);
        Assert.Equal(["AuditLogs", "Notifications", "Permissions"], targets.Keys.Order(StringComparer.Ordinal));
        foreach (var (service, database) in targets)
        {
            var resourceName = service.ToLowerInvariant();
            Assert.Matches(
                $"AddTenantWrightService\\(\"{resourceName}\"[^\\n]*\\n\\s+\\.WithServiceInfrastructure\\(infrastructure, \"{database}\", tenantConnectionProtectionKey\\)",
                appHostSource);
        }

        var targetsMethod = GetSection(extensionsSource, "WithSharedDatabaseTargets(", "WithLocalTemporal(");
        Assert.Contains("__Host", targetsMethod, StringComparison.Ordinal);
        Assert.Contains("__Port", targetsMethod, StringComparison.Ordinal);
        Assert.Contains("__Database", targetsMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", targetsMethod, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", targetsMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDatabase", targetsMethod, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every outbound internal client names the client profile its host is actually configured with.
    /// </summary>
    [Fact]
    public void Internal_clients_name_the_client_profile_their_host_is_given()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");

        var notificationsSection = GetSection(appHostSource, "var notifications =", "var workflows =");
        Assert.Contains("\"IdentityPreferencesClient__ClientProfile\", \"NotificationsInternal\"", notificationsSection, StringComparison.Ordinal);
        Assert.Contains("\"IdentityAudienceClient__ClientProfile\", \"NotificationsInternal\"", notificationsSection, StringComparison.Ordinal);
        Assert.Contains("\"OpenIddict__Clients__NotificationsInternal__Secret\"", notificationsSection, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenIddict__Clients__Internal__", notificationsSection, StringComparison.Ordinal);

        var tenantSection = GetSection(appHostSource, "var tenant =", "var permissions =");
        Assert.Contains("\"TenantImpactEnrichment__ClientProfile\", \"Internal\"", tenantSection, StringComparison.Ordinal);
        Assert.Contains(".WithEnvironment(\"OpenIddict__Clients__Internal__Secret\", internalClientSecret)", tenantSection, StringComparison.Ordinal);

        var identitySection = GetSection(appHostSource, "var identity =", "var tenant =");
        Assert.Contains("tenantwright-notifications-internal", identitySection, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fresh clone must start without manual secret setup, so every AppHost secret is generated on
    /// first run and persisted to user secrets; a hand-set user-secret value still takes precedence.
    /// </summary>
    [Fact]
    public void AppHost_secret_parameters_should_be_generated_and_persisted()
    {
        var appHostSource = RepositoryFiles.ReadAppHost("AppHost.cs");
        var secretsSource = RepositoryFiles.ReadAppHost("LocalSecrets.cs");

        Assert.DoesNotContain("builder.AddParameter(", appHostSource, StringComparison.Ordinal);

        var parameterNames = Regex
            .Matches(appHostSource, "LocalSecrets\\.Add\\w+\\(builder, \"(?<name>[^\"]+)\"\\)")
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        string[] expectedNames =
        [
            "bootstrap-host-admin-password",
            "bootstrap-tenant-admin-password",
            "internal-client-secret",
            "notifications-internal-client-secret",
            "notifications-provider-protection-key",
            "public-registration-hash-key",
            "auditlogs-seal-signing-key",
            "tenant-connection-protection-key"
        ];
        Assert.Equal(expectedNames, parameterNames);

        var parameterDeclarations = Regex.Count(secretsSource, "builder\\.AddParameter\\(");
        Assert.Equal(parameterDeclarations, Regex.Count(secretsSource, "secret: true, persist: true\\)|secret: true,\\s+persist: true\\)"));
    }

    private static string GetSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find AppHost section start '{startMarker}'.");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find AppHost section end '{endMarker}'.");

        return source[start..end];
    }
}
