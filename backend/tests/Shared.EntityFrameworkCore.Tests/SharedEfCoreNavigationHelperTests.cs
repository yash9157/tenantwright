using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore.ChangeTrackers;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public class SharedEfCoreNavigationHelperTests
{
    [Fact]
    public void GetEntityEntryIdentity_should_support_composite_entity_keys()
    {
        using var scope = CreateDbContextScope();
        var entity = new CompositeKeyEntity
        {
            KeyPart1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            KeyPart2 = 42,
            Name = "sample"
        };

        scope.DbContext.Attach(entity);
        var helper = new ExposedNavigationHelper();

        var identity = helper.GetIdentity(scope.DbContext.Entry(entity));

        Assert.NotNull(identity);
        Assert.Contains("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", identity, StringComparison.Ordinal);
        Assert.Contains("42", identity, StringComparison.Ordinal);
    }

    private static DbContextScope CreateDbContextScope()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new TestDbContext(options);
        dbContext.Database.EnsureCreated();
        return new DbContextScope(connection, dbContext);
    }

    private sealed class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeKeyEntity>(b =>
            {
                b.HasKey(x => new { x.KeyPart1, x.KeyPart2 });
                b.Property(x => x.Name).HasMaxLength(128);
            });
        }
    }

    private sealed class CompositeKeyEntity : IEntity
    {
        public Guid KeyPart1 { get; set; }

        public int KeyPart2 { get; set; }

        public string Name { get; set; } = string.Empty;

        public object?[] GetKeys() => [KeyPart1, KeyPart2];
    }

    private sealed class ExposedNavigationHelper : SharedEfCoreNavigationHelper
    {
        public string? GetIdentity(EntityEntry entityEntry)
        {
            return GetEntityEntryIdentity(entityEntry);
        }
    }

    private sealed class DbContextScope : IDisposable
    {
        private readonly SqliteConnection _connection;
        public TestDbContext DbContext { get; }

        public DbContextScope(SqliteConnection connection, TestDbContext dbContext)
        {
            _connection = connection;
            DbContext = dbContext;
        }

        public void Dispose()
        {
            DbContext.Dispose();
            _connection.Dispose();
        }
    }
}
