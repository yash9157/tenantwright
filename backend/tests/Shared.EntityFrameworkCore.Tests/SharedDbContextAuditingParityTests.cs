using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore;
using Shared.Guids;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public class SharedDbContextAuditingParityTests
{
    [Fact]
    public async Task SaveChanges_assigns_uuid_v7_to_empty_guid_entity_id()
    {
        using var harness = new TestHarness(enableMultiTenantFilter: false);
        var entity = new ParentEntity(Guid.Empty, "generated-id");

        harness.DbContext.Add(entity);
        await harness.DbContext.SaveChangesAsync();

        Assert.Equal(7, entity.Id.Version);
        Assert.NotEqual(Guid.Empty, entity.Id);
    }

    [Fact]
    public async Task SaveChanges_should_skip_audit_updates_for_fk_only_modified_entry()
    {
        using var harness = new TestHarness(enableMultiTenantFilter: false);
        var parent = new ParentEntity(Guid.NewGuid(), "parent");
        var child = new ChildEntity(Guid.NewGuid(), "child", parent.Id);
        harness.DbContext.AddRange(parent, child);
        await harness.DbContext.SaveChangesAsync();

        harness.AuditPropertySetter.Reset();

        var foreignKeyProperty = harness.DbContext.Entry(child).Property(x => x.ParentId);
        foreignKeyProperty.OriginalValue = child.ParentId;
        foreignKeyProperty.IsModified = true;
        await harness.DbContext.SaveChangesAsync();

        Assert.DoesNotContain(typeof(ChildEntity), harness.AuditPropertySetter.ModifiedEntities);
    }

    [Fact]
    public async Task SaveChanges_should_honor_disable_auditing_update_modification_props_flag()
    {
        using var harness = new TestHarness(enableMultiTenantFilter: true);
        var entity = new FlaggedEntity(
            Guid.NewGuid(),
            "before",
            "before-silent-mod",
            harness.CurrentTenant.Id);
        harness.DbContext.Add(entity);
        await harness.DbContext.SaveChangesAsync();

        harness.AuditPropertySetter.Reset();

        entity.SilentModification = "after-silent-mod";
        await harness.DbContext.SaveChangesAsync();

        Assert.Equal(0, harness.AuditPropertySetter.ModificationSetCount);
    }

    [Fact]
    public void Add_should_fail_closed_when_entity_tenant_mismatches_current_tenant()
    {
        using var harness = new TestHarness(enableMultiTenantFilter: true);
        var anotherTenantId = Guid.NewGuid();
        var entity = new FlaggedEntity(
            Guid.NewGuid(),
            "before",
            "before-silent-mod",
            anotherTenantId);

        var ex = Assert.Throws<InvalidOperationException>(() => harness.DbContext.Add(entity));
        Assert.Contains("Cross-tenant write detected", ex.Message, StringComparison.Ordinal);
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        public TestCurrentTenant CurrentTenant { get; }
        public CapturingAuditPropertySetter AuditPropertySetter { get; }
        public TestDbContext DbContext { get; }

        public TestHarness(bool enableMultiTenantFilter)
        {
            CurrentTenant = new TestCurrentTenant(Guid.NewGuid());
            AuditPropertySetter = new CapturingAuditPropertySetter();

            var services = new ServiceCollection();
            services.AddSingleton<ICurrentTenant>(CurrentTenant);
            services.AddSingleton<IDataFilter>(new TestDataFilter(enableMultiTenantFilter));
            services.AddSingleton<IAuditPropertySetter>(AuditPropertySetter);
            services.AddSingleton<IAuditingManager, NoopAuditingManager>();
            services.AddSingleton<IEntityHistoryHelper, NullEntityHistoryHelper>();
            services.AddSingleton<IUnitOfWorkManager, NoopUnitOfWorkManager>();
            services.AddSingleton<IGuidGenerator>(_ => UuidV7GuidGenerator.Instance);

            _serviceProvider = services.BuildServiceProvider();
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(_connection)
                .Options;

            DbContext = TestDbContextInitialization.Initialize(new TestDbContext(options)
            {
                ServiceProvider = _serviceProvider
            });

            DbContext.Database.EnsureCreated();
        }

        public void Dispose()
        {
            DbContext.Dispose();
            _serviceProvider.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class TestDbContext : SharedDbContext<TestDbContext>
    {
        public DbSet<ParentEntity> Parents => Set<ParentEntity>();

        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ParentEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(128);
            });

            modelBuilder.Entity<ChildEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(128);
                b.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            });

            modelBuilder.Entity<FlaggedEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(128);
                b.Property(x => x.SilentModification).HasMaxLength(128);
            });
        }
    }

    private sealed class ParentEntity : Entity<Guid>
    {
        public string Name { get; set; } = string.Empty;

        private ParentEntity()
        {
        }

        public ParentEntity(Guid id, string name)
            : base(id)
        {
            Name = name;
        }
    }

    private sealed class ChildEntity : Entity<Guid>
    {
        public string Name { get; set; } = string.Empty;
        public Guid? ParentId { get; set; }
        public ParentEntity? Parent { get; set; }

        private ChildEntity()
        {
        }

        public ChildEntity(Guid id, string name, Guid? parentId)
            : base(id)
        {
            Name = name;
            ParentId = parentId;
        }
    }

    private sealed class FlaggedEntity : Entity<Guid>, IMultiTenant
    {
        public string Name { get; set; } = string.Empty;

        [DisableAuditing(UpdateModificationProps = false)]
        public string SilentModification { get; set; } = string.Empty;

        public Guid? TenantId { get; set; }

        private FlaggedEntity()
        {
        }

        public FlaggedEntity(Guid id, string name, string silentModification, Guid? tenantId)
            : base(id)
        {
            Name = name;
            SilentModification = silentModification;
            TenantId = tenantId;
        }
    }

    private sealed class CapturingAuditPropertySetter : IAuditPropertySetter
    {
        public int CreationSetCount { get; private set; }
        public int ModificationSetCount { get; private set; }
        public int DeletionSetCount { get; private set; }
        public int IncrementEntityVersionCount { get; private set; }
        public List<Type> ModifiedEntities { get; } = [];

        public void SetCreationProperties(object entity)
        {
            CreationSetCount++;
        }

        public void SetModificationProperties(object entity)
        {
            ModificationSetCount++;
            ModifiedEntities.Add(entity.GetType());
        }

        public void SetDeletionProperties(object entity)
        {
            DeletionSetCount++;
        }

        public void IncrementEntityVersionProperty(object entity)
        {
            IncrementEntityVersionCount++;
        }

        public void Reset()
        {
            CreationSetCount = 0;
            ModificationSetCount = 0;
            DeletionSetCount = 0;
            IncrementEntityVersionCount = 0;
            ModifiedEntities.Clear();
        }
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => "tenant";

        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            Id = id;
            return new DisposeAction(() => Id = previousId);
        }
    }

    private sealed class TestDataFilter : IDataFilter
    {
        private readonly bool _enableMultiTenantFilter;

        public TestDataFilter(bool enableMultiTenantFilter)
        {
            _enableMultiTenantFilter = enableMultiTenantFilter;
        }

        public IDisposable Enable<TFilter>()
            where TFilter : class
        {
            return NoopDisposable.Instance;
        }

        public IDisposable Disable<TFilter>()
            where TFilter : class
        {
            return NoopDisposable.Instance;
        }

        public bool IsEnabled<TFilter>()
            where TFilter : class
        {
            if (typeof(TFilter) == typeof(IMultiTenant))
            {
                return _enableMultiTenantFilter;
            }

            return false;
        }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;

        public IAuditLogSaveHandle BeginScope()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NoopUnitOfWorkManager : IUnitOfWorkManager
    {
        public IUnitOfWork? Current => null;

        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false)
        {
            throw new NotSupportedException();
        }

        public IUnitOfWork Begin(bool requiresNew = false)
        {
            throw new NotSupportedException();
        }

        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false)
        {
            throw new NotSupportedException();
        }

        public void BeginReserved(string reservationName, UnitOfWorkOptions options)
        {
            throw new NotSupportedException();
        }

        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _action;

        public DisposeAction(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            _action();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
