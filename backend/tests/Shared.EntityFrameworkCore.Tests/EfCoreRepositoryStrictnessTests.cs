using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public class EfCoreRepositoryStrictnessTests
{
    [Fact]
    public async Task InsertAsync_assigns_uuid_v7_to_empty_guid_entity_id()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();
        var entity = new HostOnlyEntity(Guid.Empty, "generated-id");

        await repository.InsertAsync(entity, autoSave: true);

        Assert.Equal(7, entity.Id.Version);
        Assert.NotEqual(Guid.Empty, entity.Id);
    }

    [Fact]
    public async Task DeleteDirectAsync_removes_entities_without_materializing_list_delete_path()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();

        await repository.InsertManyAsync([
            new HostOnlyEntity(Guid.NewGuid(), "keep-1"),
            new HostOnlyEntity(Guid.NewGuid(), "remove"),
            new HostOnlyEntity(Guid.NewGuid(), "keep-2")
        ], autoSave: true);

        await repository.DeleteDirectAsync(x => x.Name == "remove");

        var remaining = await harness.DbContext.Set<HostOnlyEntity>().Select(x => x.Name).ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain("remove", remaining);
    }

    [Fact]
    public async Task InsertMany_falls_back_to_ef_path_when_bulk_provider_is_missing()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();

        await repository.InsertManyAsync([
            new HostOnlyEntity(Guid.NewGuid(), "a"),
            new HostOnlyEntity(Guid.NewGuid(), "b")
        ], autoSave: true);

        Assert.Equal(2, await harness.DbContext.Set<HostOnlyEntity>().CountAsync());
    }

    [Fact]
    public async Task Non_multi_tenant_entity_repository_resolves_dbcontext_in_host_scope()
    {
        var tenantId = Guid.NewGuid();
        using var harness = new TestHarness(currentTenantId: tenantId);

        var hostRepository = harness.CreateHostRepository();
        var tenantRepository = harness.CreateTenantRepository();

        await hostRepository.GetQueryableAsync();
        Assert.Null(harness.DbContextProvider.LastTenantIdAtGet);

        await tenantRepository.GetQueryableAsync();
        Assert.Equal(tenantId, harness.DbContextProvider.LastTenantIdAtGet);
    }

    [Fact]
    public async Task Non_multi_tenant_entity_repository_keeps_host_scope_until_async_dbcontext_resolution_completes()
    {
        var tenantId = Guid.NewGuid();
        using var harness = new TestHarness(currentTenantId: tenantId, pauseDbContextResolution: true);
        var repository = harness.CreateHostRepository();

        var resolution = repository.GetQueryableAsync();
        await harness.DbContextProvider.ResolutionStarted.WaitAsync(TimeSpan.FromSeconds(5));

        harness.DbContextProvider.ResumeResolution();
        await resolution;

        Assert.Null(harness.DbContextProvider.LastTenantIdAtGet);
        Assert.Equal(tenantId, harness.CurrentTenant.Id);
    }

    [Fact]
    public async Task EntityName_routes_repository_to_shared_type_dbset()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateAliasRepository();
        repository.EntityName = AliasEntitySetName;

        await repository.InsertAsync(new AliasEntity(Guid.NewGuid(), "alias"), autoSave: true);

        var count = await harness.DbContext.Set<AliasEntity>(AliasEntitySetName).CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task FindAsync_without_details_tracks_for_safe_read_modify_write()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();
        var entity = new HostOnlyEntity(Guid.NewGuid(), "no-tracking");

        await repository.InsertAsync(entity, autoSave: true);
        harness.DbContext.ChangeTracker.Clear();

        {
            var loaded = await repository.FindAsync(entity.Id, includeDetails: false);
            Assert.NotNull(loaded);
        }

        Assert.Contains(harness.DbContext.ChangeTracker.Entries<HostOnlyEntity>(), entry => entry.Entity.Id == entity.Id);
    }

    [Fact]
    public async Task UpdateAsync_on_a_repository_loaded_entity_marks_only_changed_properties()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();
        var entity = new HostOnlyEntity(Guid.NewGuid(), "before", rank: 7);

        await repository.InsertAsync(entity, autoSave: true);
        harness.DbContext.ChangeTracker.Clear();
        var loaded = await repository.GetAsync(entity.Id, includeDetails: false);
        loaded.Rename("after");

        await repository.UpdateAsync(loaded);

        var entry = harness.DbContext.Entry(loaded);
        Assert.True(entry.Property(x => x.Name).IsModified);
        Assert.False(entry.Property(x => x.Rank).IsModified);
    }

    [Fact]
    public async Task GetPagedListAsync_sorts_by_mapped_scalar_property()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();

        await repository.InsertManyAsync([
            new HostOnlyEntity(Guid.Parse("00000000-0000-0000-0000-000000000003"), "charlie", 1),
            new HostOnlyEntity(Guid.Parse("00000000-0000-0000-0000-000000000001"), "alpha", 1),
            new HostOnlyEntity(Guid.Parse("00000000-0000-0000-0000-000000000002"), "bravo", 1)
        ], autoSave: true);

        var result = await repository.GetPagedListAsync(0, 10, "Name desc");

        Assert.Equal(["charlie", "bravo", "alpha"], result.Select(x => x.Name));
    }

    [Fact]
    public async Task GetPagedListAsync_supports_multiple_columns_and_primary_key_tie_breaker()
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        await repository.InsertManyAsync([
            new HostOnlyEntity(secondId, "same", 2),
            new HostOnlyEntity(firstId, "same", 2),
            new HostOnlyEntity(Guid.Parse("00000000-0000-0000-0000-000000000003"), "other", 1)
        ], autoSave: true);

        var result = await repository.GetPagedListAsync(0, 10, "Rank desc, Name asc");

        Assert.Equal([firstId, secondId], result.Take(2).Select(x => x.Id));
    }

    [Theory]
    [InlineData("Missing asc")]
    [InlineData("Name.ToString()")]
    [InlineData("Name.Length")]
    public async Task GetPagedListAsync_rejects_unknown_computed_or_nested_sorting(string sorting)
    {
        using var harness = new TestHarness();
        var repository = harness.CreateHostRepository();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetPagedListAsync(0, 10, sorting));

        Assert.Contains("sorting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string AliasEntitySetName = "AliasEntities";

    private sealed class TestHarness : IDisposable
    {
        public SqliteConnection Connection { get; }
        public ServiceProvider ServiceProvider { get; }
        public TestCurrentTenant CurrentTenant { get; }
        public TestDbContext DbContext { get; }
        public TestDbContextProvider DbContextProvider { get; }

        public TestHarness(
            Guid? currentTenantId = null,
            bool pauseDbContextResolution = false)
        {
            CurrentTenant = new TestCurrentTenant(currentTenantId ?? Guid.NewGuid());

            var services = new ServiceCollection();
            services.AddSingleton(CurrentTenant);
            services.AddSingleton<ICurrentTenant>(sp => sp.GetRequiredService<TestCurrentTenant>());
            services.AddSingleton<IDataFilter, DisabledDataFilter>();
            services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
            services.AddSingleton<IAuditingManager, NoopAuditingManager>();
            services.AddSingleton<IUnitOfWorkManager, NoopUnitOfWorkManager>();
            services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
            services.AddSingleton<IClock, UtcClock>();

            ServiceProvider = services.BuildServiceProvider();

            Connection = new SqliteConnection("DataSource=:memory:");
            Connection.Open();

            var options = new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(Connection)
                .Options;

            DbContext = TestDbContextInitialization.Initialize(new TestDbContext(options)
            {
                ServiceProvider = this.ServiceProvider
            });

            DbContext.Database.EnsureCreated();
            DbContextProvider = new TestDbContextProvider(DbContext, CurrentTenant, pauseDbContextResolution);
        }

        public EfCoreRepository<TestDbContext, HostOnlyEntity, Guid> CreateHostRepository()
        {
            return new EfCoreRepository<TestDbContext, HostOnlyEntity, Guid>(DbContextProvider, ServiceProvider);
        }

        public EfCoreRepository<TestDbContext, TenantAwareEntity, Guid> CreateTenantRepository()
        {
            return new EfCoreRepository<TestDbContext, TenantAwareEntity, Guid>(DbContextProvider, ServiceProvider);
        }

        public EfCoreRepository<TestDbContext, AliasEntity, Guid> CreateAliasRepository()
        {
            return new EfCoreRepository<TestDbContext, AliasEntity, Guid>(DbContextProvider, ServiceProvider);
        }

        public void Dispose()
        {
            DbContext.Dispose();
            ServiceProvider.Dispose();
            Connection.Dispose();
        }
    }

    private sealed class TestDbContextProvider : IDbContextProvider<TestDbContext>
    {
        private readonly TestDbContext _dbContext;
        private readonly TestCurrentTenant _currentTenant;
        private readonly TaskCompletionSource? _resolutionStarted;
        private readonly TaskCompletionSource? _resumeResolution;

        public Guid? LastTenantIdAtGet { get; private set; }
        public Task ResolutionStarted => _resolutionStarted?.Task ?? Task.CompletedTask;

        public TestDbContextProvider(
            TestDbContext dbContext,
            TestCurrentTenant currentTenant,
            bool pauseResolution)
        {
            _dbContext = dbContext;
            _currentTenant = currentTenant;
            if (pauseResolution)
            {
                _resolutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _resumeResolution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task<TestDbContext> GetDbContextAsync()
        {
            if (_resolutionStarted != null && _resumeResolution != null)
            {
                _resolutionStarted.TrySetResult();
                await _resumeResolution.Task;
            }

            LastTenantIdAtGet = _currentTenant.Id;
            return _dbContext;
        }

        public void ResumeResolution()
        {
            _resumeResolution?.TrySetResult();
        }
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name { get; private set; }

        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            var previousName = Name;
            Id = id;
            Name = name;
            return new TestDisposeAction(() =>
            {
                Id = previousId;
                Name = previousName;
            });
        }
    }

    private sealed class DisabledDataFilter : IDataFilter
    {
        public IDisposable Enable<TFilter>() where TFilter : class => new TestDisposeAction(static () => { });

        public IDisposable Disable<TFilter>() where TFilter : class => new TestDisposeAction(static () => { });

        public bool IsEnabled<TFilter>() where TFilter : class => false;
    }

    private sealed class TestDisposeAction : IDisposable
    {
        private readonly Action _action;

        public TestDisposeAction(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            _action();
        }
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object entity)
        {
        }

        public void SetModificationProperties(object entity)
        {
        }

        public void SetDeletionProperties(object entity)
        {
        }

        public void IncrementEntityVersionProperty(object entity)
        {
        }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current { get; } = new NoopAuditLogScope();

        public IAuditLogSaveHandle BeginScope()
        {
            return NoopAuditLogSaveHandle.Instance;
        }
    }

    private sealed class NoopAuditLogScope : IAuditLogScope
    {
        public AuditLogInfo Log { get; } = new();
    }

    private sealed class NoopAuditLogSaveHandle : IAuditLogSaveHandle
    {
        public static readonly NoopAuditLogSaveHandle Instance = new();

        public Task SaveAsync() => Task.CompletedTask;

        public void Dispose()
        {
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

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
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

            modelBuilder.Entity<HostOnlyEntity>(builder =>
            {
                builder.ToTable("HostOnlyEntities");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).IsRequired();
                builder.Property(x => x.Rank).IsRequired();
            });

            modelBuilder.Entity<TenantAwareEntity>(builder =>
            {
                builder.ToTable("TenantAwareEntities");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).IsRequired();
            });

            modelBuilder.SharedTypeEntity<AliasEntity>(AliasEntitySetName, builder =>
            {
                builder.ToTable("AliasEntities");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).IsRequired();
            });
        }
    }

    private sealed class HostOnlyEntity : Entity<Guid>
    {
        public string Name { get; private set; }
        public int Rank { get; private set; }

        private HostOnlyEntity()
        {
            Name = string.Empty;
        }

        public HostOnlyEntity(Guid id, string name, int rank = 0)
            : base(id)
        {
            Name = name;
            Rank = rank;
        }

        public void Rename(string name)
        {
            Name = name;
        }
    }

    private sealed class TenantAwareEntity : Entity<Guid>, IMultiTenant
    {
        public Guid? TenantId { get; private set; }
        public string Name { get; private set; }

        private TenantAwareEntity()
        {
            Name = string.Empty;
        }

        public TenantAwareEntity(Guid id, string name)
            : base(id)
        {
            Name = name;
        }
    }

    private sealed class AliasEntity : Entity<Guid>
    {
        public string Name { get; private set; }

        private AliasEntity()
        {
            Name = string.Empty;
        }

        public AliasEntity(Guid id, string name)
            : base(id)
        {
            Name = name;
        }
    }
}
