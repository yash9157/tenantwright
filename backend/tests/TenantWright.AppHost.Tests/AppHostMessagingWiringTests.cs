using System.Text.RegularExpressions;
using Shared.EventBus.Messaging;
using Xunit;

namespace TenantWright.AppHost.Tests;

/// <summary>
/// Source-level checks of the AppHost's local messaging wiring. The AppHost is not started here; these pin the
/// contract that each service connects as its own principal with its own key, and that no secret lives in source.
/// </summary>
public sealed class AppHostMessagingWiringTests
{
    private static readonly string[] TopologyPrincipals =
    [
        TenantWrightMessagingTopology.AuditLogsPrincipal,
        TenantWrightMessagingTopology.AuthPrincipal,
        TenantWrightMessagingTopology.IdentityPrincipal,
        TenantWrightMessagingTopology.NotificationsPrincipal,
        TenantWrightMessagingTopology.PermissionsPrincipal,
        TenantWrightMessagingTopology.TenantPrincipal,
        TenantWrightMessagingTopology.WorkflowsPrincipal
    ];

    private static readonly string[] AppHostSourceFiles =
    [
        "AppHost.cs", "LocalInfrastructure.cs", "LocalMessaging.cs", "LocalPublicRegistration.cs",
        "LocalSecrets.cs", "TenantWrightServiceExtensions.cs", "LocalSecretFile.cs", "LocalMessagingMaterial.cs",
        "RabbitMqPasswordHash.cs", "LocalBrokerDefinitions.cs"
    ];

    [Fact]
    public void Every_service_should_connect_as_its_own_principal()
    {
        var appHost = RepositoryFiles.ReadAppHost("AppHost.cs");
        var services = Regex.Matches(
                appHost,
                "var (?<variable>\\w+) = builder\\.AddTenantWrightService\\(\"(?<name>[a-z]+)\"(?<body>.*?);\\r?\\n",
                RegexOptions.Singleline)
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["body"].Value, StringComparer.Ordinal);

        Assert.Equal(TopologyPrincipals.Length, services.Count);
        foreach (var (name, body) in services)
        {
            var wiring = Assert.Single(Regex.Matches(body, "\\.WithMessaging\\(infrastructure, \"(?<principal>[^\"]+)\"\\)"));
            Assert.Equal($"tenantwright-{name}", wiring.Groups["principal"].Value);
        }

        Assert.Equal(
            TopologyPrincipals.Order(StringComparer.Ordinal),
            services.Keys.Select(name => $"tenantwright-{name}").Order(StringComparer.Ordinal));

        var gateway = appHost[appHost.IndexOf("builder.AddTenantWrightService(\"apigateway\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain(".WithMessaging(", gateway, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_material_should_cover_exactly_the_topology_principals()
    {
        var source = RepositoryFiles.ReadAppHost("LocalMessaging.cs");
        var declaration = Regex.Match(source, "internal static readonly string\\[\\] Principals =\\s*\\[(?<items>[^\\]]+)\\]");
        Assert.True(declaration.Success);

        var principals = Regex.Matches(declaration.Groups["items"].Value, "\"(?<principal>[^\"]+)\"")
            .Select(match => match.Groups["principal"].Value)
            .ToArray();

        Assert.Equal(TopologyPrincipals.Order(StringComparer.Ordinal), principals.Order(StringComparer.Ordinal));
        Assert.Contains("LocalMessagingMaterialStore.Ensure(materialPath, Principals)", source, StringComparison.Ordinal);
        Assert.Contains("LocalBrokerDefinitions.Ensure(material, BrokerAdminUserName, virtualHost)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WithMessaging_should_give_each_service_its_own_broker_user_password_and_signing_key()
    {
        var extension = ExtractMethod(RepositoryFiles.ReadAppHost("LocalMessaging.cs"), "public static IResourceBuilder<ProjectResource> WithMessaging(");

        string[] expected =
        [
            ".WithEnvironment(\"Messaging__Provider\", messaging.Provider)",
            ".WithEnvironment(\"Messaging__ServiceName\", principal)",
            ".WithEnvironment(\"Messaging__ProducerPrincipal\", principal)",
            ".WithEnvironment(\"Messaging__RabbitMq__HostName\", ReferenceExpression.Create($\"{endpoint.Property(EndpointProperty.Host)}\"))",
            ".WithEnvironment(\"Messaging__RabbitMq__Port\", ReferenceExpression.Create($\"{endpoint.Property(EndpointProperty.Port)}\"))",
            ".WithEnvironment(\"Messaging__RabbitMq__VirtualHost\", messaging.VirtualHost)",
            ".WithEnvironment(\"Messaging__RabbitMq__UserName\", principal)",
            ".WithEnvironment(\"Messaging__RabbitMq__Password\", messaging.BrokerPassword(principal))",
            ".WithEnvironment(\"Messaging__RabbitMq__UseTls\", \"false\")",
            ".WithEnvironment(\"Messaging__RabbitMq__AllowInsecureTransport\", \"true\")",
            ".WithEnvironment(\"Messaging__Trust__ActiveKeyId\", identity.KeyId)",
            ".WithEnvironment(\"Messaging__Trust__PrivateKeyPath\", identity.PrivateKeyPath)",
            ".WithEnvironment($\"Messaging__Trust__PublicKeys__{index}__KeyId\", key.KeyId)",
            ".WithEnvironment($\"Messaging__Trust__PublicKeys__{index}__Actor\", key.Principal)",
            ".WithEnvironment($\"Messaging__Trust__PublicKeys__{index}__PublicKeyPath\", key.PublicKeyPath)",
            ".WithEnvironment($\"Messaging__Trust__PublicKeys__{index}__Revoked\", \"false\")",
            "service.WithEnvironment(\"MT_LICENSE_PATH\", licensePath)",
            "var identity = messaging.Material.For(principal);",
            "var trusted = messaging.Material.Principals;",
            "return service.WaitFor(broker);",
            "return service.WithEnvironment(\"Messaging__Provider\", LocalMessaging.NoneProvider);"
        ];

        foreach (var line in expected)
        {
            Assert.Contains(line, extension, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Local_messaging_should_default_to_wolverine_and_fail_closed_outside_development()
    {
        var source = RepositoryFiles.ReadAppHost("LocalMessaging.cs");

        Assert.Contains("return WolverineProvider;", ExtractMethod(source, "private static string ReadProvider("), StringComparison.Ordinal);
        Assert.Contains(
            "if (!builder.Environment.IsDevelopment() || !builder.ExecutionContext.IsRunMode)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("? RequireReadOnlyFile(configuration, $\"{Section}:MassTransitLicensePath\")", source, StringComparison.Ordinal);
        // Broker passwords reach services as secret parameters, masked on the dashboard and never persisted.
        Assert.Equal(2, Regex.Count(source, "builder\\.AddParameter\\([^;]*secret: true\\)"));
        Assert.DoesNotContain("persist: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_broker_should_be_a_persistent_dynamically_ported_container_provisioned_from_definitions()
    {
        var source = RepositoryFiles.ReadAppHost("LocalInfrastructure.cs");
        var broker = ExtractMethod(source, "private static IResourceBuilder<RabbitMQServerResource> AddRabbitMq(");

        string[] expected =
        [
            "port: ReadPort(configuration, \"RabbitMq:Port\")",
            ".WithManagementPlugin(port: ReadPort(configuration, \"RabbitMq:ManagementPort\"))",
            ".WithImageTag(RabbitMqImageTag)",
            ".WithContainerName(\"tenantwright-rabbitmq\")",
            ".WithDataVolume(\"tenantwright-rabbitmq-data\")",
            ".WithBindMount(brokerFiles.DefinitionsPath, LocalBrokerDefinitions.ContainerDefinitionsPath, isReadOnly: true)",
            ".WithBindMount(brokerFiles.ConfigurationPath, LocalBrokerDefinitions.ContainerConfigurationPath, isReadOnly: true)",
            ".WithEnvironment(BrokerDefinitionsFingerprintVariable, brokerFiles.Fingerprint)",
            ".WithLifetime(ContainerLifetime.Persistent)"
        ];
        foreach (var line in expected)
        {
            Assert.Contains(line, broker, StringComparison.Ordinal);
        }

        Assert.Contains("var rabbitMq = messaging.Enabled ? AddRabbitMq(builder, messaging) : null;", source, StringComparison.Ordinal);

        var chartImage = Regex.Match(
            File.ReadAllText(Path.Combine(RepositoryFiles.Root, "charts", "tenantwright-rabbitmq", "values.yaml")),
            "image: rabbitmq:(?<tag>[^@\\s]+)");
        Assert.True(chartImage.Success);
        Assert.Contains($"private const string RabbitMqImageTag = \"{chartImage.Groups["tag"].Value}\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Temporal_should_default_on_with_messaging_and_be_wired_into_workflows()
    {
        var infrastructure = RepositoryFiles.ReadAppHost("LocalInfrastructure.cs");
        var appHost = RepositoryFiles.ReadAppHost("AppHost.cs");
        var extensions = RepositoryFiles.ReadAppHost("TenantWrightServiceExtensions.cs");

        Assert.Contains(
            "if (configuration.GetValue<bool?>($\"{Section}:Temporal:Enabled\") ?? messaging.Enabled)",
            infrastructure,
            StringComparison.Ordinal);
        var workflows = appHost[appHost.IndexOf("var workflows =", StringComparison.Ordinal)..];
        Assert.Contains(".WithLocalTemporal(infrastructure)", workflows[..workflows.IndexOf(';', StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(appHost, "\\.WithLocalTemporal\\("));
        Assert.Contains(".WithEnvironment(\"Temporal__TargetHost\", infrastructure.TemporalTargetHost)", extensions, StringComparison.Ordinal);
        Assert.Contains(".WaitFor(infrastructure.Temporal)", extensions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hand_made_local_messaging_contract_should_be_gone()
    {
        Assert.False(File.Exists(RepositoryFiles.AppHostPath("LocalOnboardingRuntime.cs")));
        Assert.False(File.Exists(Path.Combine(RepositoryFiles.Root, "scripts", "setup-local-onboarding.ps1")));

        foreach (var file in AppHostSourceFiles)
        {
            var source = RepositoryFiles.ReadAppHost(file);
            Assert.DoesNotContain("WithLocalMessaging", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setup-local-onboarding", source, StringComparison.Ordinal);
            Assert.DoesNotContain(":Services:", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LocalMessaging:Enabled", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PublicRegistrationHashKey", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every credential is generated at run time. The only credential-shaped literals allowed are Cloudflare's
    /// documented always-pass Turnstile test pair, which Auth itself accepts only in Development.
    /// </summary>
    [Fact]
    public void AppHost_sources_should_contain_no_secret_literals()
    {
        string[] documentedTestValues = ["1x00000000000000000000AA", "1x0000000000000000000000000000000AA"];
        var secretShaped = new Regex("^(?:[0-9a-fA-F]{32,}|(?=\\S*\\d)[A-Za-z0-9+/]{24,}={0,2})$", RegexOptions.CultureInvariant);

        foreach (var file in AppHostSourceFiles)
        {
            var source = RepositoryFiles.ReadAppHost(file);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN EC PRIVATE KEY", source, StringComparison.Ordinal);

            foreach (Match literal in Regex.Matches(source, "\"(?<value>[^\"\\\\\\r\\n]*)\""))
            {
                var value = literal.Groups["value"].Value;
                if (documentedTestValues.Contains(value, StringComparer.Ordinal))
                {
                    Assert.Equal("LocalPublicRegistration.cs", file);
                    continue;
                }

                Assert.False(secretShaped.IsMatch(value), $"{file} contains a credential-shaped literal.");
            }
        }
    }

    /// <summary>Returns the source of the member starting at <paramref name="signature"/>, up to its matching brace.</summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}'.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
