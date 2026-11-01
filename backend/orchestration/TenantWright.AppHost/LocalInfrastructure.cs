using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace TenantWright.AppHost;

/// <summary>
/// Local development infrastructure owned by the AppHost: one PostgreSQL server (one database per
/// service), Valkey, and, while local messaging is enabled, RabbitMQ and a Temporal development server.
/// Containers are persistent and use named volumes, so data survives AppHost restarts. Host ports are
/// allocated by Aspire (shown on the dashboard); set
/// <c>LocalInfrastructure:{Postgres|Valkey|RabbitMq|Temporal}:Port</c> (and
/// <c>LocalInfrastructure:RabbitMq:ManagementPort</c> / <c>LocalInfrastructure:Temporal:UiPort</c>) to pin one
/// for tools such as pgAdmin. <c>LocalInfrastructure:Temporal:Enabled</c> defaults to whether local messaging is
/// enabled, because tenant onboarding runs as a Temporal workflow driven by messages.
/// </summary>
internal sealed class LocalInfrastructure
{
    private const string Section = "LocalInfrastructure";
    private const string TemporalImage = "temporalio/temporal";
    private const string TemporalImageTag = "1.9.1";
    private const int TemporalGrpcPort = 7233;
    private const int TemporalUiPort = 8233;
    private const string TemporalStateDirectory = "/home/temporal";

    /// <summary>The RabbitMQ release <c>charts/tenantwright-rabbitmq/values.yaml</c> pins, with the management plugin.</summary>
    private const string RabbitMqImageTag = "4.3.6-management";

    /// <summary>
    /// Environment variable carrying the generated broker files' fingerprint. It is part of the persistent
    /// container's configuration, so changed provisioning recreates the container and the node re-imports.
    /// </summary>
    private const string BrokerDefinitionsFingerprintVariable = "TENANTWRIGHT_LOCAL_BROKER_DEFINITIONS_SHA256";

    private LocalInfrastructure(
        IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ValkeyResource> valkey,
        LocalMessaging messaging,
        IResourceBuilder<RabbitMQServerResource>? rabbitMq,
        IResourceBuilder<ContainerResource>? temporal,
        string temporalNamespace)
    {
        Postgres = postgres;
        Valkey = valkey;
        Messaging = messaging;
        RabbitMq = rabbitMq;
        Temporal = temporal;
        TemporalNamespace = temporalNamespace;
    }

    public IResourceBuilder<PostgresServerResource> Postgres { get; }

    public IResourceBuilder<ValkeyResource> Valkey { get; }

    public LocalMessaging Messaging { get; }

    /// <summary>The local broker, present exactly when <see cref="Messaging"/> is enabled.</summary>
    public IResourceBuilder<RabbitMQServerResource>? RabbitMq { get; }

    public IResourceBuilder<ContainerResource>? Temporal { get; }

    public string TemporalNamespace { get; }

    public static LocalInfrastructure Add(IDistributedApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        var postgres = builder.AddPostgres("postgres", port: ReadPort(configuration, "Postgres:Port"))
            .WithContainerName("tenantwright-postgres")
            .WithDataVolume("tenantwright-postgres-data")
            .WithLifetime(ContainerLifetime.Persistent);

        var valkey = builder.AddValkey("valkey", port: ReadPort(configuration, "Valkey:Port"))
            .WithContainerName("tenantwright-valkey")
            .WithDataVolume("tenantwright-valkey-data")
            .WithLifetime(ContainerLifetime.Persistent);

        var messaging = LocalMessaging.Create(builder);
        var rabbitMq = messaging.Enabled ? AddRabbitMq(builder, messaging) : null;

        var temporalNamespace = configuration[$"{Section}:Temporal:Namespace"] ?? "tenantwright-development";
        IResourceBuilder<ContainerResource>? temporal = null;
        if (configuration.GetValue<bool?>($"{Section}:Temporal:Enabled") ?? messaging.Enabled)
        {
            temporal = builder.AddContainer("temporal", TemporalImage, TemporalImageTag)
                .WithContainerName("tenantwright-temporal")
                .WithArgs(
                    "server", "start-dev",
                    "--ip", "0.0.0.0",
                    "--namespace", temporalNamespace,
                    "--db-filename", $"{TemporalStateDirectory}/temporal.db")
                // The image runs as the unprivileged 'temporal' user. A new named volume mounted on its existing home
                // directory inherits that ownership; a volume on a path absent from the image is root-owned and the
                // dev server cannot create its SQLite database.
                .WithVolume("tenantwright-temporal-state", TemporalStateDirectory)
                .WithEndpoint(
                    port: ReadPort(configuration, "Temporal:Port"),
                    targetPort: TemporalGrpcPort,
                    name: "grpc",
                    scheme: "tcp")
                .WithHttpEndpoint(
                    port: ReadPort(configuration, "Temporal:UiPort"),
                    targetPort: TemporalUiPort,
                    name: "ui")
                .WithLifetime(ContainerLifetime.Persistent);
        }

        return new LocalInfrastructure(postgres, valkey, messaging, rabbitMq, temporal, temporalNamespace);
    }

    public IResourceBuilder<PostgresDatabaseResource> AddDatabase(string resourceName, string databaseName)
        => Postgres.AddDatabase(resourceName, databaseName);

    public ReferenceExpression ValkeyConfiguration
        => ReferenceExpression.Create($"{Valkey.Resource.PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

    public ReferenceExpression ValkeyPassword
        => Valkey.Resource.PasswordParameter is { } password
            ? ReferenceExpression.Create($"{password}")
            : throw new InvalidOperationException("The local Valkey resource must be password protected.");

    public ReferenceExpression TemporalTargetHost
        => Temporal is null
            ? throw new InvalidOperationException("Temporal is not enabled in LocalInfrastructure.")
            : ReferenceExpression.Create($"{Temporal.GetEndpoint("grpc").Property(EndpointProperty.HostAndPort)}");

    /// <summary>
    /// The local broker. Users, vhosts, permissions and the queue policy come from the generated definitions
    /// (<see cref="LocalBrokerDefinitions"/>), imported on every boot, so the persistent container always matches the
    /// material directory. The definitions also declare the administrator, because a node that imports definitions
    /// on its first boot does not create the image's default user.
    /// </summary>
    private static IResourceBuilder<RabbitMQServerResource> AddRabbitMq(
        IDistributedApplicationBuilder builder,
        LocalMessaging messaging)
    {
        var configuration = builder.Configuration;
        var adminUser = builder.AddParameter("rabbitmq-admin-username", LocalMessaging.BrokerAdminUserName);
        var brokerFiles = messaging.BrokerFiles;

        return builder.AddRabbitMQ("rabbitmq", adminUser, messaging.BrokerAdminPassword, port: ReadPort(configuration, "RabbitMq:Port"))
            .WithManagementPlugin(port: ReadPort(configuration, "RabbitMq:ManagementPort"))
            .WithImageTag(RabbitMqImageTag)
            .WithContainerName("tenantwright-rabbitmq")
            .WithDataVolume("tenantwright-rabbitmq-data")
            .WithBindMount(brokerFiles.DefinitionsPath, LocalBrokerDefinitions.ContainerDefinitionsPath, isReadOnly: true)
            .WithBindMount(brokerFiles.ConfigurationPath, LocalBrokerDefinitions.ContainerConfigurationPath, isReadOnly: true)
            .WithEnvironment(BrokerDefinitionsFingerprintVariable, brokerFiles.Fingerprint)
            .WithLifetime(ContainerLifetime.Persistent);
    }

    /// <summary>
    /// Returns a configured fixed host port, or <see langword="null"/> so Aspire allocates a free one.
    /// Dynamic ports are the default because a fixed port that another process already publishes is
    /// not an error for the container runtime: clients would silently reach that other server.
    /// </summary>
    private static int? ReadPort(ConfigurationManager configuration, string key)
    {
        var value = configuration[$"{Section}:{key}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new InvalidOperationException($"AppHost setting '{Section}:{key}' must be a TCP port between 1 and 65535.");
    }
}
