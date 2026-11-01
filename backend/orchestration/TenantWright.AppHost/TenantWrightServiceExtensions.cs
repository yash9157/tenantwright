namespace TenantWright.AppHost;

internal static class TenantWrightServiceExtensions
{
    /// <summary>
    /// Adds a backend host with fixed local ports and the shared Development runtime switches. Launch
    /// profiles are excluded so the AppHost is the single source of local configuration.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddTenantWrightService(
        this IDistributedApplicationBuilder builder,
        string name,
        string projectPath,
        int httpPort,
        int httpsPort)
        => builder.AddProject(name, projectPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint(port: httpPort, name: "http")
            .WithHttpsEndpoint(port: httpsPort, name: "https")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", string.Empty)
            .WithEnvironment("__ASPNETCORE_BROWSER_TOOLS", "false")
            .WithEnvironment("DOTNET_STARTUP_HOOKS", string.Empty);

    /// <summary>
    /// Marks a backend service that the gateway and other services reach over plain HTTP, as in the Helm charts:
    /// TLS terminates at Auth and the gateway, so the service must answer HTTP rather than redirect it to HTTPS.
    /// A redirect would make <c>HttpClient</c> drop the <c>Authorization</c> header on the scheme change and turn
    /// every internal call into a 401. The service's HTTPS endpoint stays available for direct local use.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AsInternalService(this IResourceBuilder<ProjectResource> service)
        => service.WithEnvironment("Hosting__HttpsRedirection__Enabled", "false");

    /// <summary>Key id of the generated local tenant connection-string protection key.</summary>
    internal const string TenantConnectionProtectionKeyId = "tenant-local-v1";

    /// <summary>
    /// Gives a service its own database on the local PostgreSQL server plus the shared Valkey, applies
    /// its EF Core migrations on start, and shares the tenant connection-string protection key ring.
    /// </summary>
    /// <remarks>
    /// Dedicated tenant connection strings are encrypted by the Tenant service and decrypted by every
    /// service that resolves them, so every service receives the same AES-256-GCM key. Plaintext
    /// values are refused, as in deployed environments.
    /// </remarks>
    public static IResourceBuilder<ProjectResource> WithServiceInfrastructure(
        this IResourceBuilder<ProjectResource> service,
        LocalInfrastructure infrastructure,
        string databaseName,
        IResourceBuilder<ParameterResource> tenantConnectionProtectionKey)
    {
        var database = infrastructure.AddDatabase($"{service.Resource.Name}-db", databaseName);

        return service
            .WithEnvironment("ConnectionStrings__Default", database)
            .WithEnvironment("EfCore__AutoMigrate", "true")
            .WithEnvironment("Caching__Valkey__Configuration", infrastructure.ValkeyConfiguration)
            .WithEnvironment("Caching__Valkey__Password", infrastructure.ValkeyPassword)
            .WithEnvironment("TenantConnectionStrings__Protection__CurrentKeyId", TenantConnectionProtectionKeyId)
            .WithEnvironment("TenantConnectionStrings__Protection__CurrentKeyVersion", "1")
            .WithEnvironment("TenantConnectionStrings__Protection__RequireProtectedValues", "true")
            .WithEnvironment(
                $"TenantConnectionStrings__Protection__Keys__{TenantConnectionProtectionKeyId}",
                tenantConnectionProtectionKey)
            .WaitFor(database)
            .WaitFor(infrastructure.Valkey);
    }

    /// <summary>
    /// Tells the Tenant service where the dedicatable services' shared databases live on the local server, as targets
    /// only (the PostgreSQL endpoint host and port plus each database name), so it can refuse a dedicated assignment to
    /// one of them. No credential is passed: Tenant never connects to these databases.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithSharedDatabaseTargets(
        this IResourceBuilder<ProjectResource> tenant,
        LocalInfrastructure infrastructure,
        params (string Service, string Database)[] targets)
    {
        var endpoint = infrastructure.Postgres.Resource.PrimaryEndpoint;
        foreach (var (service, database) in targets)
        {
            var prefix = $"TenantDatabases__SharedTargets__{service}";
            tenant
                .WithEnvironment($"{prefix}__Host", ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Host)}"))
                .WithEnvironment($"{prefix}__Port", ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Port)}"))
                .WithEnvironment($"{prefix}__Database", database);
        }

        return tenant;
    }

    /// <summary>Points Workflows at the local Temporal development server when it is enabled.</summary>
    public static IResourceBuilder<ProjectResource> WithLocalTemporal(
        this IResourceBuilder<ProjectResource> service,
        LocalInfrastructure infrastructure)
    {
        if (infrastructure.Temporal is null)
        {
            return service;
        }

        return service
            .WithEnvironment("Temporal__Enabled", "true")
            .WithEnvironment("Temporal__TargetHost", infrastructure.TemporalTargetHost)
            .WithEnvironment("Temporal__Namespace", infrastructure.TemporalNamespace)
            .WaitFor(infrastructure.Temporal);
    }

    public static IResourceBuilder<ProjectResource> WithGatewayDestination(
        this IResourceBuilder<ProjectResource> gateway,
        string cluster,
        IResourceBuilder<ProjectResource> destination)
        => gateway.WithEnvironment(
            $"ReverseProxy__Clusters__{cluster}__Destinations__{cluster}_1__Address",
            destination.GetEndpoint("http"));
}
