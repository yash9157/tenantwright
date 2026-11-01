using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore.Modeling;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public class ExtraPropertyDictionaryModelMappingTests
{
    [Fact]
    public void SharedDbContext_model_build_should_not_fail_for_aggregate_root_with_extra_properties()
    {
        using var scope = CreateDbContextScope();

        var exception = Record.Exception(() => scope.DbContext.Database.EnsureCreated());

        Assert.Null(exception);
    }

    [Fact]
    public void SharedDbContext_model_should_not_include_ExtraPropertyDictionary_as_entity_type()
    {
        using var scope = CreateDbContextScope();

        _ = scope.DbContext.Model;

        Assert.Null(scope.DbContext.Model.FindEntityType(typeof(ExtraPropertyDictionary)));
    }

    [Fact]
    public void SharedDbContext_model_should_keep_ExtraProperties_as_scalar_property_mapping()
    {
        using var scope = CreateDbContextScope();
        var entityType = scope.DbContext.Model.FindEntityType(typeof(TestAggregateRoot));

        Assert.NotNull(entityType);
        Assert.NotNull(entityType!.FindProperty(nameof(IHasExtraProperties.ExtraProperties)));
        Assert.Null(entityType.FindNavigation(nameof(IHasExtraProperties.ExtraProperties)));
    }

    private static DbContextScope CreateDbContextScope()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new TestDbContext(options);
        return new DbContextScope(connection, dbContext);
    }

    private sealed class TestDbContext : SharedDbContext<TestDbContext>
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TestAggregateRoot>(b =>
            {
                b.ToTable("TestAggregateRoots");
                b.HasKey(x => x.Id);
                b.ConfigureByConvention();
                b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            });
        }
    }

    private sealed class TestAggregateRoot : AggregateRoot<Guid>
    {
        public string Name { get; private set; } = string.Empty;

        private TestAggregateRoot()
        {
        }

        public TestAggregateRoot(Guid id, string name)
            : base(id)
        {
            Name = name;
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
