using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shared.Domain;
using Shared.EntityFrameworkCore.Messaging;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public sealed class SharedDbContextSafetyTests
{
    [Fact]
    public async Task SaveChangesAsync_refuses_a_business_write_not_initialized_for_a_unit_of_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<UninitializedDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new UninitializedDbContext(options);
        dbContext.GuardedEntities.Add(new GuardedEntity { Id = Guid.NewGuid() });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dbContext.SaveChangesAsync());

        Assert.Contains("was not initialized by IDbContextProvider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveChangesAsync_allows_a_noop_without_unit_of_work_initialization()
    {
        var options = new DbContextOptionsBuilder<UninitializedDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var dbContext = new UninitializedDbContext(options);

        Assert.Equal(0, await dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// The neutral outbox has no transport-owned bypass: an outbox row persists only through a context a unit of work
    /// initialized, so it can never commit apart from the business change that produced it.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_refuses_an_outbox_row_not_initialized_for_a_unit_of_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<UninitializedMessagingDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new UninitializedMessagingDbContext(options);
        dbContext.Set<MessagingOutboxMessage>().Add(MessagingOutboxMessage.Create(
            new Shared.EventBus.Messaging.TenantWrightOutboundMessage(
                Guid.CreateVersion7(),
                typeof(Shared.EventContracts.TenantDeactivatedEto),
                "Shared.EventContracts.TenantDeactivatedEto",
                Shared.EventBus.Messaging.TenantWrightMessageRouteKind.Event,
                null,
                "{}"u8.ToArray(),
                new Dictionary<string, string>(),
                null,
                new Shared.EventBus.Messaging.OriginAttestation(
                    Guid.Empty, "contract", "tenantwright-tests", "host", null, DateTimeOffset.UtcNow, "digest", "key", "sig")),
            DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());

        Assert.Contains("was not initialized by IDbContextProvider", exception.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Model_finalization_applies_the_tenant_filter_to_a_late_mapped_entity()
    {
        var options = new DbContextOptionsBuilder<LateMappedTenantDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new LateMappedTenantDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(LateMappedTenantEntity));

        Assert.NotNull(entityType);
        Assert.NotEmpty(entityType!.GetRootType().GetDeclaredQueryFilters());
    }

    private sealed class UninitializedDbContext(DbContextOptions<UninitializedDbContext> options)
        : SharedDbContext<UninitializedDbContext>(options)
    {
        public DbSet<GuardedEntity> GuardedEntities => Set<GuardedEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<GuardedEntity>().HasKey(entity => entity.Id);
        }
    }

    private sealed class UninitializedMessagingDbContext(DbContextOptions<UninitializedMessagingDbContext> options)
        : SharedDbContext<UninitializedMessagingDbContext>(options)
    {
        public DbSet<GuardedEntity> GuardedEntities => Set<GuardedEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<GuardedEntity>().HasKey(entity => entity.Id);
            modelBuilder.AddTenantWrightMessagingEntities();
        }
    }

    private sealed class GuardedEntity
    {
        public Guid Id { get; set; }
    }

    private sealed class LateMappedTenantDbContext(DbContextOptions<LateMappedTenantDbContext> options)
        : SharedDbContext<LateMappedTenantDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<LateMappedTenantEntity>().HasKey(entity => entity.Id);
        }
    }

    private sealed class LateMappedTenantEntity : IMultiTenant
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
    }
}
