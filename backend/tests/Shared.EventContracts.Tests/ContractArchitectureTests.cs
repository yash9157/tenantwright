using System.Xml.Linq;
using Shared.EventContracts;
using Shared.Events;
using Xunit;

namespace Shared.EventContracts.Tests;

public sealed class ContractArchitectureTests
{
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.Ordinal)
    {
        "Actions",
        "ClientIpAddress",
        "DecisionReason",
        "DisplayName",
        "Email",
        "EntityChanges",
        "Exceptions",
        "Reason",
        "Recipients",
        "RequesterDisplayName",
        "SessionId",
        "SuggestedUserName",
        "TargetEmail",
        "TemplateModel",
        "TenantName",
        "UserAgent",
        "UserName"
    };

    [Fact]
    public void Contract_project_should_reference_only_the_approved_shared_primitive_project()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "shared",
            "Shared.EventContracts",
            "Shared.EventContracts.csproj");
        var project = XDocument.Load(projectPath);
        var packageReferences = project.Descendants("PackageReference").ToArray();
        var projectReferences = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
            .ToArray();

        Assert.Empty(packageReferences);
        Assert.Equal(
            new[] { "../Shared.Domain.Shared/Shared.Domain.Shared.csproj" },
            projectReferences);
    }

    [Fact]
    public void Contract_source_should_not_import_runtime_or_service_implementation_dependencies()
    {
        string[] forbiddenTerms =
        [
            "MassTransit",
            "RabbitMQ",
            "EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Shared.EventBus",
            "Shared.Uow",
            "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Logging",
            "Temporalio"
        ];
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "shared", "Shared.EventContracts");
        var source = string.Join(
            "\n",
            Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        foreach (var forbiddenTerm in forbiddenTerms)
        {
            Assert.DoesNotContain(forbiddenTerm, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sensitive_payload_shapes_should_never_be_classified_as_internal()
    {
        var contracts = typeof(AuditLogWriteRequestedTenantEto).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.IsDefined(typeof(DistributedContractAttribute), inherit: false));

        foreach (var contractType in contracts)
        {
            var hasSensitiveProperty = contractType.GetProperties()
                .Any(property => SensitivePropertyNames.Contains(property.Name));
            var metadata = contractType.GetCustomAttributes(typeof(DistributedContractAttribute), inherit: false)
                .Cast<DistributedContractAttribute>()
                .Single();

            if (hasSensitiveProperty)
            {
                Assert.Equal(DistributedContractDataClassification.Sensitive, metadata.DataClassification);
            }
        }
    }

    [Fact]
    public void Contract_payloads_should_not_expose_forbidden_secret_or_free_text_fields()
    {
        string[] forbiddenPropertyFragments =
        [
            "AccessToken",
            "FreeTextNote",
            "ConnectionString",
            "Password",
            "RefreshToken",
            "Secret"
        ];
        var contractProperties = typeof(AuditLogWriteRequestedTenantEto).Assembly
            .GetTypes()
            .Where(type => type.IsClass)
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        foreach (var fragment in forbiddenPropertyFragments)
        {
            Assert.DoesNotContain(contractProperties, property => property.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TenantWright.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the TenantWright repository root.");
    }
}
