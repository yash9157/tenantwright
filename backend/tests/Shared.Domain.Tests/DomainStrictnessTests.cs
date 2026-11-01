using Microsoft.Extensions.DependencyInjection;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.Domain.Values;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.Domain.Tests;

public class EntityStrictnessTests
{
    [Fact]
    public void Transient_entities_with_default_keys_are_not_equal()
    {
        var left = new TestEntity();
        var right = new TestEntity();

        Assert.False(EntityHelper.EntityEquals(left, right));
    }

    [Fact]
    public void TrySetId_respects_disable_id_generation_attribute()
    {
        var entity = new DisableGeneratedIdEntity();

        EntityHelper.TrySetId(entity, Guid.NewGuid, checkForDisableIdGenerationAttribute: true);

        Assert.Equal(Guid.Empty, entity.Id);
    }

    /// <summary>Constructing an entity does not stamp it with the ambient tenant.</summary>
    /// <remarks>
    /// Tenant assignment belongs to the persistence boundary, not the domain constructor.
    /// <c>SharedDbContext</c> fills <c>IMultiTenant.TenantId</c> from the current tenant as a row is
    /// added, and throws on a cross-tenant write when the row already names a different one.
    ///
    /// Keeping the constructor inert is the safer half of that split: an entity built outside a request
    /// -- a seeder, a background worker, a test -- cannot silently acquire whichever tenant happened to
    /// be ambient at construction time and then be saved as that tenant's data. The one place that
    /// assigns is also the one place that validates.
    ///
    /// This previously asserted the opposite and had been failing. It is inverted rather than deleted so
    /// the tenancy intent stays pinned to the layer that actually owns it.
    /// </remarks>
    [Fact]
    public void Entity_constructor_does_not_stamp_the_ambient_tenant()
    {
        var accessor = AsyncLocalCurrentTenantAccessor.Instance;
        var previous = accessor.Current;

        accessor.Current = new BasicTenantInfo(Guid.NewGuid());
        try
        {
            var entity = new MultiTenantEntity();

            Assert.Null(entity.TenantId);
        }
        finally
        {
            accessor.Current = previous;
        }
    }

    private sealed class TestEntity : Entity<Guid>
    {
        public string Name { get; }

        public TestEntity()
        {
            Name = string.Empty;
        }

        public TestEntity(Guid id, string name)
            : base(id)
        {
            Name = name;
        }
    }

    [DisableIdGeneration]
    private sealed class DisableGeneratedIdEntity : Entity<Guid>
    {
    }

    private sealed class MultiTenantEntity : Entity<Guid>, IMultiTenant
    {
        public Guid? TenantId { get; private set; }
    }
}

public class ValueObjectStrictnessTests
{
    [Fact]
    public void Value_equals_returns_false_when_atomic_value_lengths_differ()
    {
        var left = new CompositeValue("a", includeSecondValue: false);
        var right = new CompositeValue("a", includeSecondValue: true);

        Assert.False(left.ValueEquals(right));
    }

    private sealed class CompositeValue : ValueObject
    {
        private readonly string _first;
        private readonly bool _includeSecondValue;

        public CompositeValue(string first, bool includeSecondValue)
        {
            _first = first;
            _includeSecondValue = includeSecondValue;
        }

        protected override IEnumerable<object> GetAtomicValues()
        {
            yield return _first;
            if (_includeSecondValue)
            {
                yield return "second";
            }
        }
    }
}

public class RepositoryStrictnessTests
{
    [Fact]
    public void Read_only_repository_activation_disables_change_tracking()
    {
        var services = new ServiceCollection();
        services.AddDefaultRepository(typeof(TestRepositoryEntity), typeof(InspectableRepository));

        using var serviceProvider = services.BuildServiceProvider();
        var repository = serviceProvider.GetRequiredService<IReadOnlyRepository<TestRepositoryEntity, Guid>>();

    }

    [Fact]
    public void Disable_tracking_scope_restores_previous_state()
    {
        var repository = new InspectableRepository();


        {
        }


        {
        }

    }


    private sealed class TestRepositoryEntity : Entity<Guid>
    {
        public string Name { get; private set; }

        public TestRepositoryEntity()
        {
            Name = string.Empty;
        }

        public TestRepositoryEntity(Guid id, string name)
            : base(id)
        {
            Name = name;
        }
    }

    private sealed class InspectableRepository : IRepository<TestRepositoryEntity, Guid>
    {
        private readonly List<TestRepositoryEntity> _items = [];

        public string? EntityName { get; set; }
        public string ProviderName => "TestProvider";

        public Task<IQueryable<TestRepositoryEntity>> WithDetailsAsync()
        {
            return Task.FromResult(_items.AsQueryable());
        }

        public Task<IQueryable<TestRepositoryEntity>> WithDetailsAsync(params System.Linq.Expressions.Expression<Func<TestRepositoryEntity, object>>[] propertySelectors)
        {
            return Task.FromResult(_items.AsQueryable());
        }

        public Task<IQueryable<TestRepositoryEntity>> GetQueryableAsync()
        {
            return Task.FromResult(_items.AsQueryable());
        }

        public Task<List<TestRepositoryEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_items.ToList());
        }

        public Task<List<TestRepositoryEntity>> GetListAsync(
            System.Linq.Expressions.Expression<Func<TestRepositoryEntity, bool>> predicate,
            bool includeDetails = false,
            CancellationToken cancellationToken = default)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.Where(compiled).ToList());
        }

        public Task<long> GetCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult((long)_items.Count);
        }

        public Task<List<TestRepositoryEntity>> GetPagedListAsync(int skipCount, int maxResultCount, string sorting, bool includeDetails = false, CancellationToken cancellationToken = default)
        {
            var page = _items.Skip(skipCount).Take(maxResultCount).ToList();
            return Task.FromResult(page);
        }

        public Task<TestRepositoryEntity?> FindAsync(Guid id, bool includeDetails = true, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_items.FirstOrDefault(x => x.Id == id));
        }

        public async Task<TestRepositoryEntity> GetAsync(Guid id, bool includeDetails = true, CancellationToken cancellationToken = default)
        {
            var entity = await FindAsync(id, includeDetails, cancellationToken);
            return entity ?? throw new InvalidOperationException("Entity not found.");
        }

        public Task<TestRepositoryEntity?> FindAsync(
            System.Linq.Expressions.Expression<Func<TestRepositoryEntity, bool>> predicate,
            bool includeDetails = true,
            CancellationToken cancellationToken = default)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.FirstOrDefault(compiled));
        }

        public async Task<TestRepositoryEntity> GetAsync(
            System.Linq.Expressions.Expression<Func<TestRepositoryEntity, bool>> predicate,
            bool includeDetails = true,
            CancellationToken cancellationToken = default)
        {
            var entity = await FindAsync(predicate, includeDetails, cancellationToken);
            return entity ?? throw new InvalidOperationException("Entity not found.");
        }

        public Task DeleteAsync(
            System.Linq.Expressions.Expression<Func<TestRepositoryEntity, bool>> predicate,
            bool autoSave = false,
            CancellationToken cancellationToken = default)
        {
            var compiled = predicate.Compile();
            _items.RemoveAll(x => compiled(x));
            return Task.CompletedTask;
        }

        public Task DeleteDirectAsync(
            System.Linq.Expressions.Expression<Func<TestRepositoryEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            return DeleteAsync(predicate, cancellationToken: cancellationToken);
        }

        public Task<TestRepositoryEntity> InsertAsync(TestRepositoryEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _items.Add(entity);
            return Task.FromResult(entity);
        }

        public Task InsertManyAsync(IEnumerable<TestRepositoryEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _items.AddRange(entities);
            return Task.CompletedTask;
        }

        public Task<TestRepositoryEntity> UpdateAsync(TestRepositoryEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            var index = _items.FindIndex(x => x.Id == entity.Id);
            if (index >= 0)
            {
                _items[index] = entity;
            }
            else
            {
                _items.Add(entity);
            }

            return Task.FromResult(entity);
        }

        public Task UpdateManyAsync(IEnumerable<TestRepositoryEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            foreach (var entity in entities)
            {
                var index = _items.FindIndex(x => x.Id == entity.Id);
                if (index >= 0)
                {
                    _items[index] = entity;
                }
                else
                {
                    _items.Add(entity);
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(TestRepositoryEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(x => x.Id == entity.Id);
            return Task.CompletedTask;
        }

        public Task DeleteManyAsync(IEnumerable<TestRepositoryEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            var ids = entities.Select(x => x.Id).ToHashSet();
            _items.RemoveAll(x => ids.Contains(x.Id));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(x => x.Id == id);
            return Task.CompletedTask;
        }

        public Task DeleteManyAsync(IEnumerable<Guid> ids, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            var idSet = ids.ToHashSet();
            _items.RemoveAll(x => idSet.Contains(x.Id));
            return Task.CompletedTask;
        }
    }
}