using Xunit;

namespace AuditLogs.Tests;

public class AuditArchiveManifestMigrationContinuityTests
{
    [Fact]
    public void Clean_initial_migration_should_create_archive_manifest_table_and_index()
    {
        var migrationsDirectory = Path.Combine(
            FindRepositoryRoot(),
            "services",
            "AuditLogs",
            "AuditLogs.EntityFrameworkCore",
            "Migrations");
        var migrationPath = Assert.Single(Directory.GetFiles(migrationsDirectory, "*_Initial.cs"));
        var content = File.ReadAllText(migrationPath);

        Assert.Contains("name: \"AuditArchiveManifests\"", content, StringComparison.Ordinal);
        Assert.Contains("name: \"IX_AuditArchiveManifests_PartitionKey\"", content, StringComparison.Ordinal);
        Assert.Contains("name: \"InboxState\"", content, StringComparison.Ordinal);
        Assert.Contains("name: \"OutboxMessage\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("IncomingEvents", content, StringComparison.Ordinal);
        Assert.DoesNotContain("OutgoingEvents", content, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var solutionPath = Path.Combine(current.FullName, "TenantWright.slnx");
            if (File.Exists(solutionPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root containing TenantWright.slnx was not found.");
    }
}
