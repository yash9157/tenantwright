using System.Text.RegularExpressions;
using Shared.EventContracts;
using Shared.Events;
using Xunit;

namespace Shared.EventBus.Tests;

public class EventContractArchitectureTests
{
    private static readonly Regex EtoDeclarationPattern = new(
        @"\b(?:class|record)\s+\w*Eto\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(typeof(IdentitySessionRevokedTenantEto), "Shared.EventContracts.IdentitySessionRevokedTenantEto")]
    [InlineData(typeof(IdentitySessionRevokedHostEto), "Shared.EventContracts.IdentitySessionRevokedHostEto")]
    public void Identity_session_revoked_events_should_have_stable_wire_names(Type eventType, string expectedName)
    {
        var attribute = Assert.Single(
            eventType
                .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
                .Cast<EventNameAttribute>());

        Assert.Equal(expectedName, attribute.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Identity_session_revoked_events_should_identify_the_exact_session(bool tenantScoped)
    {
        object eventData = tenantScoped
            ? new IdentitySessionRevokedTenantEto
            {
                TenantId = Guid.NewGuid(),
                SessionId = "session-123"
            }
            : new IdentitySessionRevokedHostEto
            {
                SessionId = "session-123"
            };

        var sessionId = eventData switch
        {
            IdentitySessionRevokedTenantEto tenant => tenant.SessionId,
            IdentitySessionRevokedHostEto host => host.SessionId,
            _ => throw new InvalidOperationException("Unexpected session-revocation contract type.")
        };

        Assert.Equal("session-123", sessionId);
    }

    [Fact]
    public void Concrete_cross_service_etos_should_declare_an_explicit_event_name()
    {
        var repositoryRoot = FindRepositoryRoot();
        var missingEventNames = EnumerateSourceFiles(repositoryRoot, "app", "services", "shared")
            .Where(IsConcreteEtoSource)
            .Where(file => !File.ReadAllText(file).Contains("[EventName(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(repositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missingEventNames);
    }

    [Fact]
    public void Application_and_service_code_should_not_disable_deferred_or_outbox_publication()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = EnumerateSourceFiles(repositoryRoot, "app", "services")
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(candidate =>
                candidate.Line.Contains("useOutbox: false", StringComparison.Ordinal) ||
                candidate.Line.Contains("onUnitOfWorkComplete: false", StringComparison.Ordinal))
            .Select(candidate =>
                $"{Path.GetRelativePath(repositoryRoot, candidate.File)}:{candidate.Number}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool IsConcreteEtoSource(string file)
    {
        var source = File.ReadAllText(file);
        return EtoDeclarationPattern.IsMatch(source) &&
               !source.Contains("abstract class", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string repositoryRoot,
        params string[] topLevelDirectories)
    {
        return topLevelDirectories.SelectMany(directory =>
            Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory),
                "*.cs",
                SearchOption.AllDirectories));
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
