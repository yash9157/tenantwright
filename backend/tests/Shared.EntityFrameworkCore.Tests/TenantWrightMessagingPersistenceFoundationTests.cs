using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>Container-free proofs of the neutral messaging model and options.</summary>
public sealed class TenantWrightMessagingPersistenceFoundationTests
{
    [Fact]
    public void Model_registration_maps_only_the_neutral_messaging_tables()
    {
        using var dbContext = new MessagingModelProbeDbContext(
            new DbContextOptionsBuilder<MessagingModelProbeDbContext>().UseNpgsql("Host=localhost;Database=model_probe").Options);
        var model = dbContext.Model;

        Assert.DoesNotContain(model.GetEntityTypes(), entity => entity.ClrType.Namespace?.StartsWith("MassTransit", StringComparison.Ordinal) == true);

        var outbox = model.FindEntityType(typeof(MessagingOutboxMessage))!;
        Assert.Equal("MessagingOutboxMessages", outbox.GetTableName());
        Assert.Equal("public", outbox.GetSchema());
        Assert.Equal(nameof(MessagingOutboxMessage.Id), outbox.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal("bytea", outbox.FindProperty(nameof(MessagingOutboxMessage.Payload))!.GetColumnType());
        Assert.Equal("jsonb", outbox.FindProperty(nameof(MessagingOutboxMessage.Headers))!.GetColumnType());
        Assert.Equal(256, outbox.FindProperty(nameof(MessagingOutboxMessage.LastErrorType))!.GetMaxLength());
        var pending = Assert.Single(outbox.GetIndexes(), index => index.GetDatabaseName() == "IX_MessagingOutboxMessages_Pending");
        Assert.Equal(["AvailableAtUtc", "Id"], pending.Properties.Select(property => property.Name));
        Assert.Equal("\"DispatchedAtUtc\" IS NULL AND \"ParkedAtUtc\" IS NULL", pending.GetFilter());
        Assert.Contains(outbox.GetIndexes(), index => index.GetDatabaseName() == "IX_MessagingOutboxMessages_DispatchedAtUtc");

        var inbox = model.FindEntityType(typeof(MessagingInboxRecord))!;
        Assert.Equal("MessagingInboxRecords", inbox.GetTableName());
        Assert.Equal(
            [nameof(MessagingInboxRecord.MessageId), nameof(MessagingInboxRecord.Endpoint)],
            inbox.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(inbox.GetIndexes(), index => index.GetDatabaseName() == "IX_MessagingInboxRecords_ProcessedAtUtc");

        var replayState = model.FindEntityType(typeof(TenantWrightReplayState))!;
        Assert.Equal("MessagingReplayState", replayState.GetTableName());
        Assert.Equal(nameof(TenantWrightReplayState.ReplayId), replayState.FindPrimaryKey()!.Properties.Single().Name);
    }

    [Fact]
    public void Every_service_db_context_should_map_the_neutral_messaging_tables_through_the_shared_extension()
    {
        var repositoryRoot = FindRepositoryRoot();
        var contexts = new[]
        {
            "app/Auth/Auth/EntityFrameworkCore/AuthDbContext.cs",
            "services/AuditLogs/AuditLogs.EntityFrameworkCore/EntityFrameworkCore/AuditLogsDbContextApp.cs",
            "services/Identity/Identity.EntityFrameworkCore/EntityFrameworkCore/IdentityDbContextApp.cs",
            "services/Notifications/Notifications.EntityFrameworkCore/EntityFrameworkCore/NotificationsDbContextApp.cs",
            "services/Permissions/Permissions.EntityFrameworkCore/EntityFrameworkCore/PermissionsDbContextApp.cs",
            "services/Tenant/Tenant.EntityFrameworkCore/EntityFrameworkCore/TenantDbContextApp.cs",
            "services/Workflows/Workflows.EntityFrameworkCore/EntityFrameworkCore/WorkflowsDbContextApp.cs"
        };

        Assert.All(contexts, relative =>
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, relative));
            Assert.Contains("modelBuilder.AddTenantWrightMessagingEntities();", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MassTransit", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Outbox_options_defaults_should_be_valid_and_documented_bounds_enforced()
    {
        var validator = new TenantWrightOutboxOptionsValidator();
        var defaults = new TenantWrightOutboxOptions();

        Assert.True(validator.Validate(null, defaults).Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.PollInterval);
        Assert.Equal(TimeSpan.FromDays(7), defaults.DispatchedRetention);
        Assert.False(validator.Validate(null, new TenantWrightOutboxOptions { BatchSize = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new TenantWrightOutboxOptions { MaxAttempts = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new TenantWrightOutboxOptions
        {
            DispatchTimeout = TimeSpan.FromMinutes(1),
            LeaseDuration = TimeSpan.FromMinutes(1)
        }).Succeeded);
        Assert.False(validator.Validate(null, new TenantWrightOutboxOptions
        {
            RetryBaseDelay = TimeSpan.FromMinutes(1),
            RetryMaxDelay = TimeSpan.FromSeconds(1)
        }).Succeeded);

        var inboxValidator = new TenantWrightInboxOptionsValidator();
        Assert.True(inboxValidator.Validate(null, new TenantWrightInboxOptions()).Succeeded);
        Assert.Equal(TimeSpan.FromHours(24), new TenantWrightInboxOptions().RetentionBeyondOriginAge);
        Assert.False(inboxValidator.Validate(null, new TenantWrightInboxOptions { CleanupBatchSize = 0 }).Succeeded);
    }

    [Fact]
    public void Invalid_outbox_options_should_fail_when_resolved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TenantWrightOutboxOptions.SectionName}:BatchSize"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddTenantWrightMessagingPersistence<MessagingModelProbeDbContext>(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<TenantWrightOutboxOptions>>().Value);
    }

    [Theory]
    [InlineData(1, 1000, 500)]
    [InlineData(2, 2000, 1000)]
    [InlineData(4, 8000, 4000)]
    [InlineData(30, 300000, 150000)]
    public void Retry_delay_should_back_off_exponentially_within_jitter_and_the_cap(int attempts, int ceilingMs, int floorMs)
    {
        var options = new TenantWrightOutboxOptions();

        for (var sample = 0; sample < 50; sample++)
        {
            var delay = TenantWrightOutboxDispatcher<MessagingModelProbeDbContext>.RetryDelay(attempts, options);
            Assert.InRange(delay.TotalMilliseconds, floorMs, ceilingMs);
        }
    }

    [Fact]
    public void Base_host_configuration_must_not_silently_migrate_production_schemas()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new[] { "app", "gateway", "services" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory), "appsettings.json", SearchOption.AllDirectories))
            .Where(file => !IsGeneratedPath(file))
            .Where(file => File.ReadAllText(file).Contains("\"AutoMigrate\": true", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(repositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool IsGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantWright.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the TenantWright repository root.");
    }

    internal sealed class MessagingModelProbeDbContext(DbContextOptions<MessagingModelProbeDbContext> options)
        : SharedDbContext<MessagingModelProbeDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.AddTenantWrightMessagingEntities();
        }
    }
}
