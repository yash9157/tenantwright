using Xunit;

namespace Workflows.Tests;

public class WorkflowsMigrationContinuityTests
{
    [Fact]
    public void InitialMigration_Should_Exist_And_Contain_Core_Table_Markers()
    {
        var repoRoot = FindRepositoryRoot();
        var migrationDirectory = Path.Combine(
            repoRoot,
            "services",
            "Workflows",
            "Workflows.EntityFrameworkCore",
            "Migrations");

        Assert.True(Directory.Exists(migrationDirectory), $"Migrations folder not found: {migrationDirectory}");

        var migrationFile = Directory
            .GetFiles(migrationDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(migrationFile), "No migration file found for Workflows.");

        var migrationText = File.ReadAllText(migrationFile!);
        Assert.Contains("InboxState", migrationText, StringComparison.Ordinal);
        Assert.Contains("OutboxMessage", migrationText, StringComparison.Ordinal);
        Assert.Contains("OutboxState", migrationText, StringComparison.Ordinal);
        Assert.Contains("MessagingReplayState", migrationText, StringComparison.Ordinal);
        Assert.DoesNotContain("SagaInstances", migrationText, StringComparison.Ordinal);
        Assert.DoesNotContain("OutgoingEvents", migrationText, StringComparison.Ordinal);
        Assert.DoesNotContain("IncomingEvents", migrationText, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TenantWright.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root (TenantWright.slnx).");
    }
}
