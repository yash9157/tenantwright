using System.Text.RegularExpressions;
using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class MessagingArchitectureTests
{
    private static readonly Regex ForbiddenSharedCapability = new(
        @"\b(?:IStateMachineSaga|MassTransitStateMachine|RoutingSlip|AddMediator|AddRider|UsingKafka|AddJobConsumer|AddSagaStateMachine|MultiBus)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RawApplicationBus = new(
        @"\b(?:IBus|IBusControl|IPublishEndpoint|ISendEndpointProvider|IMessageBus|IMessageContext)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyEventBusApi = new(
        @"\b(?:RabbitMqDistributedEventBus|IDistributedEventBus|AddDistributedEvent|AddOrReplaceDistributedEvent|PublishDistributedEventsAsync|GetDistributedEvents|ClearDistributedEvents|RemoveDistributedEvents|IUnitOfWorkEventPublisher|UnitOfWorkEventRecord|IGeneratesDomainEvents|DomainEventRecord|AddLocalEvent|AddOrReplaceLocalEvent|EntityChangeEventHelper|EntityCreatedEventData|EntityUpdatedEventData|EntityDeletedEventData|SharedDistributedEntityEventOptions|EntityCreatedEto|EntityUpdatedEto|EntityDeletedEto|EntityEto)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Consumption is declared once, in the topology, and bound by the shared registration; services implement only
    /// <see cref="ITenantWrightMessageHandler{TMessage}"/>.
    /// </summary>
    private static readonly Regex HostOwnedTransportBinding = new(
        @"\bnew\s+TenantWrightReceiveEndpoint\s*\(|\bIConsumer\s*<|\bConsumeContext\s*<|\bConfigureConsumer\s*[<(]|\bAddConsumer\s*[<(]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Shared_runtime_should_not_enable_unapproved_mass_transit_capabilities()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = MessagingSourceDirectories(repositoryRoot)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(candidate => ForbiddenSharedCapability.IsMatch(candidate.Line))
            .Select(candidate => $"{Path.GetRelativePath(repositoryRoot, candidate.File)}:{candidate.Number}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_and_service_projects_should_not_bypass_the_shared_send_publish_boundary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new[] { "app", "services" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(candidate => RawApplicationBus.IsMatch(candidate.Line))
            .Select(candidate => $"{Path.GetRelativePath(repositoryRoot, candidate.File)}:{candidate.Number}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Legacy_event_bus_api_should_not_return()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new[] { "app", "services", "shared" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(candidate => LegacyEventBusApi.IsMatch(candidate.Line))
            .Select(candidate => $"{Path.GetRelativePath(repositoryRoot, candidate.File)}:{candidate.Number}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("AddTenantWrightMassTransit(", "shared/Shared.EventBus.MassTransit/")]
    [InlineData("AddTenantWrightMessagingCore(", "shared/Shared.EventBus.Hosting/",
        "shared/Shared.EventBus/Messaging/TenantWrightMessagingRegistration.cs")]
    [InlineData("AddTenantWrightMessagingPersistence<", "shared/Shared.EventBus.Hosting/",
        "shared/Shared.EntityFrameworkCore/Messaging/TenantWrightMessagingPersistenceExtensions.cs")]
    [InlineData("new MassTransitMessagingProvider(", "shared/Shared.EventBus.Hosting/")]
    [InlineData("AddTenantWrightWolverine(", "shared/Shared.EventBus.Wolverine/")]
    [InlineData("AddWolverine(", "shared/Shared.EventBus.Wolverine/")]
    [InlineData("new WolverineMessagingProvider(", "shared/Shared.EventBus.Hosting/")]
    public void Messaging_composition_should_happen_only_in_its_owning_project(string call, params string[] owners)
    {
        var repositoryRoot = FindRepositoryRoot();
        var callers = new[] { "app", "gateway", "orchestration", "services", "shared" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file) && !owners.Any(owner =>
                RelativePath(repositoryRoot, file).StartsWith(owner, StringComparison.Ordinal)))
            .Where(file => File.ReadAllText(file).Contains(call, StringComparison.Ordinal))
            .Select(file => RelativePath(repositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Hosts compose messaging only through AddTenantWrightMessagingHost, which selects exactly one provider by
        // Messaging:Provider and derives the host's endpoints from the topology. Composing a provider, the core or the
        // persistence directly would let a host hand-pick a catalog or run two providers.
        Assert.Empty(callers);
    }

    [Fact]
    public void Every_messaging_host_should_compose_through_the_provider_selecting_entry_point()
    {
        var repositoryRoot = FindRepositoryRoot();
        var hosts = new[]
        {
            "app/Auth/Auth/AuthHostRegistrationExtensions.cs",
            "services/AuditLogs/AuditLogs/AuditLogsHostModule.cs",
            "services/Identity/Identity/IdentityHostModule.cs",
            "services/Notifications/Notifications/NotificationsHostModule.cs",
            "services/Permissions/Permissions/PermissionsHostModule.cs",
            "services/Tenant/Tenant/TenantHostModule.cs",
            "services/Workflows/Workflows/WorkflowsHostModule.cs"
        };

        Assert.All(hosts, relative =>
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, relative));
            Assert.Contains(".AddTenantWrightMessagingHost<", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MassTransit", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Hosts_should_not_declare_receive_endpoints_or_transport_consumers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new[] { "app", "services", "shared" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file) && !IsMessagingInfrastructure(repositoryRoot, file))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(candidate => HostOwnedTransportBinding.IsMatch(candidate.Line))
            .Select(candidate => $"{Path.GetRelativePath(repositoryRoot, candidate.File)}:{candidate.Number}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Mass_transit_packages_should_use_the_single_exact_repository_version()
    {
        var repositoryRoot = FindRepositoryRoot();
        var properties = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.props"));
        var packages = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Packages.props"));

        Assert.Contains("<MassTransitVersion>9.2.0</MassTransitVersion>", properties, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(packages, "Version=\"\\[\\$\\(MassTransitVersion\\)\\]\""));
        Assert.DoesNotContain("MassTransit.EntityFrameworkCore", packages, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_mass_transit_provider_project_should_reference_a_mass_transit_package()
    {
        var repositoryRoot = FindRepositoryRoot();
        var referencing = new[] { "app", "gateway", "orchestration", "services", "shared" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "*.csproj", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), "<PackageReference\\s+Include=\"MassTransit"))
            .Select(file => RelativePath(repositoryRoot, file))
            .ToArray();

        Assert.Equal("shared/Shared.EventBus.MassTransit/Shared.EventBus.MassTransit.csproj", Assert.Single(referencing));
    }

    [Fact]
    public void Only_the_wolverine_provider_project_should_reference_a_wolverine_package()
    {
        var repositoryRoot = FindRepositoryRoot();
        var referencing = new[] { "app", "gateway", "orchestration", "services", "shared" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "*.csproj", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), "<PackageReference\\s+Include=\"(?:WolverineFx|JasperFx)"))
            .Select(file => RelativePath(repositoryRoot, file))
            .ToArray();

        Assert.Equal("shared/Shared.EventBus.Wolverine/Shared.EventBus.Wolverine.csproj", Assert.Single(referencing));
    }

    [Fact]
    public void Wolverine_packages_should_use_the_single_exact_repository_version()
    {
        var repositoryRoot = FindRepositoryRoot();
        var properties = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.props"));
        var packages = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Packages.props"));

        Assert.Matches(@"<WolverineVersion>\d+\.\d+\.\d+</WolverineVersion>", properties);
        Assert.Equal(2, Regex.Count(packages, "Version=\"\\[\\$\\(WolverineVersion\\)\\]\""));

        // Wolverine may only ever be the transport and consumer runtime: no Wolverine persistence or EF integration.
        Assert.DoesNotMatch(@"Include=""WolverineFx\.(?!RabbitMQ"")", packages);
    }

    [Fact]
    public void Wolverine_provider_should_disable_conventional_discovery_and_never_run_jasperfx_commands()
    {
        var registration = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "shared", "Shared.EventBus.Wolverine", "TenantWrightWolverineRegistration.cs"));

        Assert.Contains("options.Discovery.DisableConventionalDiscovery();", registration, StringComparison.Ordinal);
        Assert.Contains("ExtensionDiscovery.ManualOnly", registration, StringComparison.Ordinal);
        Assert.Contains("\"ready\"", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("RunJasperFxCommands", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistMessagesWith", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("UseEntityFrameworkCoreTransactions", registration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shared/Shared.EventBus/Shared.EventBus.csproj")]
    [InlineData("shared/Shared.EntityFrameworkCore/Shared.EntityFrameworkCore.csproj")]
    public void Neutral_core_and_persistence_projects_should_not_reference_a_transport(string project)
    {
        var references = Regex.Matches(
                File.ReadAllText(Path.Combine(FindRepositoryRoot(), project)),
                "<(?:PackageReference|ProjectReference)\\s+Include=\"(?<include>[^\"]+)\"")
            .Select(match => match.Groups["include"].Value)
            .ToArray();

        // The trust model, outbox, inbox and delivery guarantees are provider-neutral: only a provider project may
        // bring a transport package, so the Wolverine and MassTransit providers can share this core unchanged.
        Assert.NotEmpty(references);
        Assert.DoesNotContain(references, include => include.Contains("MassTransit", StringComparison.Ordinal));
        Assert.DoesNotContain(references, include => include.Contains("RabbitMQ", StringComparison.Ordinal));
        Assert.DoesNotContain(references, include => include.Contains("Shared.EventBus.Hosting", StringComparison.Ordinal));
    }

    [Fact]
    public void Neutral_core_assembly_should_not_reference_a_transport_assembly()
    {
        var references = typeof(TenantWrightMessageCatalog).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("MassTransit", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("RabbitMQ", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Shared.EventBus.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("shared/Shared.EventBus")]
    [InlineData("shared/Shared.EntityFrameworkCore")]
    public void Neutral_core_and_persistence_sources_should_not_use_a_transport_library(string project)
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = Directory.EnumerateFiles(Path.Combine(repositoryRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsGeneratedPath(file))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(candidate => Regex.IsMatch(
                candidate.Line, @"^\s*using\s+(?:global::)?(?:MassTransit|RabbitMQ)\b|\b(?:global::)?MassTransit\.|\bRabbitMQ\.Client\b"))
            .Select(candidate => $"{RelativePath(repositoryRoot, candidate.File)}:{candidate.Number}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Broker_health_should_be_readiness_only_and_shutdown_should_be_bounded()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registration = File.ReadAllText(Path.Combine(
            repositoryRoot, "shared", "Shared.EventBus.MassTransit", "TenantWrightMassTransitRegistration.cs"));
        var options = new MessagingDeliveryOptions();

        Assert.Contains("health.Tags.Add(\"ready\")", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("health.Tags.Add(\"live\")", registration, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StopTimeout);
    }

    /// <summary>The shared messaging core and the transport provider that binds topology to consumers.</summary>
    private static bool IsMessagingInfrastructure(string repositoryRoot, string path)
    {
        var relative = RelativePath(repositoryRoot, path);
        return relative.StartsWith("shared/Shared.EventBus/Messaging/", StringComparison.Ordinal) ||
               relative.StartsWith("shared/Shared.EventBus.MassTransit/", StringComparison.Ordinal) ||
               relative.StartsWith("shared/Shared.EventBus.Wolverine/", StringComparison.Ordinal);
    }

    private static IEnumerable<string> MessagingSourceDirectories(string repositoryRoot) =>
    [
        Path.Combine(repositoryRoot, "shared", "Shared.EventBus"),
        Path.Combine(repositoryRoot, "shared", "Shared.EventBus.MassTransit"),
        Path.Combine(repositoryRoot, "shared", "Shared.EventBus.Wolverine"),
        Path.Combine(repositoryRoot, "shared", "Shared.EventBus.Hosting")
    ];

    private static string RelativePath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

    private static bool IsGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TenantWright.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the TenantWright repository root.");
    }
}
