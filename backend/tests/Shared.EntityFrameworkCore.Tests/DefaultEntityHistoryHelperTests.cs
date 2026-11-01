using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore.ChangeTrackers;
using Shared.CurrentUser;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public class DefaultEntityHistoryHelperTests
{
    [Fact]
    public void CreateChangeList_should_exclude_base_and_disabled_properties()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;
        var entity = new HistoryEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "before",
            Secret = "before-secret",
            LastModificationTime = DateTime.UtcNow.AddMinutes(-10),
            LastModifierId = Guid.NewGuid()
        };

        db.Attach(entity);
        entity.Name = "after";
        entity.Secret = "after-secret";
        entity.LastModificationTime = DateTime.UtcNow;
        entity.LastModifierId = Guid.NewGuid();
        db.ChangeTracker.DetectChanges();

        var helper = CreateHelper();
        var changes = helper.CreateChangeList(db.ChangeTracker.Entries().ToList());

        var change = Assert.Single(changes);
        Assert.Equal("Updated", change.ChangeType);
        Assert.Contains(change.PropertyChanges, x => x.PropertyName == nameof(HistoryEntity.Name));
        Assert.DoesNotContain(change.PropertyChanges, x => x.PropertyName == nameof(HistoryEntity.Secret));
        Assert.DoesNotContain(change.PropertyChanges, x => x.PropertyName == nameof(HistoryEntity.LastModificationTime));
        Assert.DoesNotContain(change.PropertyChanges, x => x.PropertyName == nameof(HistoryEntity.LastModifierId));
    }

    [Fact]
    public void CreateChangeList_should_include_only_audited_property_for_disable_auditing_entity()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;
        var entity = new DisabledEntity
        {
            Id = Guid.NewGuid(),
            Public = "before",
            Hidden = "before-hidden"
        };

        db.Attach(entity);
        entity.Public = "after";
        entity.Hidden = "after-hidden";
        db.ChangeTracker.DetectChanges();

        var helper = CreateHelper();
        var changes = helper.CreateChangeList(db.ChangeTracker.Entries().ToList());

        var change = Assert.Single(changes);
        Assert.Equal("Updated", change.ChangeType);
        Assert.Contains(change.PropertyChanges, x => x.PropertyName == nameof(DisabledEntity.Public));
        Assert.DoesNotContain(change.PropertyChanges, x => x.PropertyName == nameof(DisabledEntity.Hidden));
    }

    [Fact]
    public void CreateChangeList_should_map_created_updated_deleted_change_types()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;

        var created = new HistoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "created"
        };
        db.Add(created);

        var updated = new HistoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "before"
        };
        db.Attach(updated);
        updated.Name = "after";

        var deleted = new HistoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "to-delete",
            IsDeleted = false
        };
        db.Attach(deleted);
        deleted.IsDeleted = true;

        db.ChangeTracker.DetectChanges();

        var helper = CreateHelper();
        var changes = helper.CreateChangeList(db.ChangeTracker.Entries().ToList());
        var mapped = changes.ToDictionary(x => x.EntityId!, x => x.ChangeType, StringComparer.Ordinal);

        Assert.Equal("Created", mapped[created.Id.ToString("D")]);
        Assert.Equal("Updated", mapped[updated.Id.ToString("D")]);
        Assert.Equal("Deleted", mapped[deleted.Id.ToString("D")]);
    }

    [Fact]
    public void CreateChangeList_should_include_complex_property_and_exclude_disabled_complex_property()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;
        var entity = new ComplexHistoryEntity
        {
            Id = Guid.NewGuid(),
            Contact = new ContactInfo
            {
                Street = "before-street",
                Secret = "before-secret"
            }
        };

        db.Attach(entity);
        entity.Contact.Street = "after-street";
        entity.Contact.Secret = "after-secret";
        db.ChangeTracker.DetectChanges();

        var helper = CreateHelper();
        var changes = helper.CreateChangeList(db.ChangeTracker.Entries().ToList());

        var change = Assert.Single(changes);
        Assert.Equal("Updated", change.ChangeType);
        Assert.Contains(change.PropertyChanges, x => x.PropertyName == "Contact.Street");
        Assert.DoesNotContain(change.PropertyChanges, x => x.PropertyName == "Contact.Secret");
    }

    [Fact]
    public void CreateChangeList_should_skip_property_changes_for_soft_deleted_update()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;
        var entity = new HistoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "before",
            IsDeleted = false
        };

        db.Attach(entity);
        entity.Name = "after";
        entity.IsDeleted = true;
        db.ChangeTracker.DetectChanges();

        var helper = CreateHelper();
        var changes = helper.CreateChangeList(db.ChangeTracker.Entries().ToList());

        var change = Assert.Single(changes);
        Assert.Equal("Deleted", change.ChangeType);
        Assert.Empty(change.PropertyChanges);
    }

    [Fact]
    public void CreateChangeList_should_include_navigation_change_with_key_values()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;

        var oldChild = new NavigationChild { Id = Guid.NewGuid(), Name = "old-child" };
        var newChild = new NavigationChild { Id = Guid.NewGuid(), Name = "new-child" };
        var root = new NavigationRoot
        {
            Id = Guid.NewGuid(),
            Name = "root",
            Child = oldChild
        };

        db.Attach(oldChild);
        db.Attach(newChild);
        db.Attach(root);
        root.Child = newChild;
        db.ChangeTracker.DetectChanges();

        var rootEntry = db.Entry(root);
        var navEntry = rootEntry.Navigations.Single();
        var trackedNavigation = new SharedNavigationEntry(navEntry, navEntry.Metadata.Name)
        {
            IsModified = true,
            OriginalValue = oldChild
        };

        var helper = CreateHelper();
        helper.InitializeNavigationHelper(new FakeNavigationHelper(trackedNavigation));

        var changes = helper.CreateChangeList([rootEntry]);

        var change = Assert.Single(changes);
        var propertyChange = Assert.Single(change.PropertyChanges.Where(x => x.PropertyName == "Child"));
        Assert.Equal("Child", propertyChange.PropertyName);
        Assert.Contains(oldChild.Id.ToString("D"), propertyChange.OriginalValue, StringComparison.Ordinal);
        Assert.Contains(newChild.Id.ToString("D"), propertyChange.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateChangeList_should_refresh_change_time_from_modification_time()
    {
        using var scope = CreateDbContextScope();
        var db = scope.DbContext;
        var entity = new HistoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "before"
        };

        db.Attach(entity);
        entity.Name = "after";
        db.ChangeTracker.DetectChanges();

        var helper = CreateHelper();
        var changes = helper.CreateChangeList(db.ChangeTracker.Entries().ToList());

        var persistedModificationTime = DateTime.UtcNow.AddMinutes(-1);
        entity.LastModificationTime = persistedModificationTime;
        helper.UpdateChangeList(changes);

        var change = Assert.Single(changes);
        Assert.Equal("Updated", change.ChangeType);
        Assert.Equal(persistedModificationTime, change.ChangeTime);
    }

    private static DefaultEntityHistoryHelper CreateHelper()
    {
        var options = Options.Create(new SharedAuditingOptions
        {
            SaveEntityHistoryWhenNavigationChanges = true
        });

        return new DefaultEntityHistoryHelper(
            new TestCurrentUser(),
            new TestCurrentTenant(),
            new EnabledAuditingHelper(),
            new JsonAuditSerializer(options),
            options);
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
            modelBuilder.Entity<HistoryEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(128);
            });

            modelBuilder.Entity<DisabledEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Public).HasMaxLength(128);
                b.Property(x => x.Hidden).HasMaxLength(128);
            });

            modelBuilder.Entity<ComplexHistoryEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Contact, cb =>
                {
                    cb.Property(x => x.Street).HasMaxLength(128);
                    cb.Property(x => x.Secret).HasMaxLength(128);
                });
            });

            modelBuilder.Entity<NavigationRoot>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(128);
                b.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildId);
            });

            modelBuilder.Entity<NavigationChild>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(128);
            });
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

    private sealed class TestCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? Id => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public string? UserName => "tester";
        public string? Name => "tester";
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public string? SessionId => null;
        public string[] Roles => [];
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => [];
        public System.Security.Claims.Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => true;
        public Guid? Id => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public string? Name => "tenant";
        public IDisposable Change(Guid? id, string? name = null) => NoopDisposable.Instance;
    }

    private sealed class EnabledAuditingHelper : IAuditingHelper
    {
        public bool ShouldSaveAudit(
            System.Reflection.MethodInfo? methodInfo,
            bool defaultValue = false,
            bool ignoreIntegrationServiceAttribute = false)
        {
            return true;
        }

        public bool IsEntityHistoryEnabled(Type entityType, bool defaultValue = false)
        {
            return true;
        }

        public AuditLogInfo CreateAuditLogInfo()
        {
            return new AuditLogInfo();
        }

        public AuditLogActionInfo CreateAuditLogAction(Type? type, System.Reflection.MethodInfo method, object?[] arguments)
        {
            return new AuditLogActionInfo();
        }

        public AuditLogActionInfo CreateAuditLogAction(
            Type? type,
            System.Reflection.MethodInfo method,
            IDictionary<string, object?> arguments)
        {
            return new AuditLogActionInfo();
        }

        public IDisposable DisableAuditing()
        {
            return NoopDisposable.Instance;
        }

        public bool IsAuditingEnabled()
        {
            return true;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class HistoryEntity : IEntity<Guid>, IAuditedObject, ISoftDelete, IMultiTenant
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public string Name { get; set; } = string.Empty;

        [DisableAuditing]
        public string Secret { get; set; } = string.Empty;

        public DateTime CreationTime { get; set; }
        public Guid? CreatorId { get; set; }
        public DateTime? LastModificationTime { get; set; }
        public Guid? LastModifierId { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletionTime { get; set; }
        public Guid? DeleterId { get; set; }

        public object?[] GetKeys() => [Id];
    }

    [DisableAuditing]
    private sealed class DisabledEntity : IEntity<Guid>
    {
        public Guid Id { get; set; }

        [Audited]
        public string Public { get; set; } = string.Empty;

        public string Hidden { get; set; } = string.Empty;

        public object?[] GetKeys() => [Id];
    }

    private sealed class ComplexHistoryEntity : IEntity<Guid>
    {
        public Guid Id { get; set; }

        public ContactInfo Contact { get; set; } = new();

        public object?[] GetKeys() => [Id];
    }

    private sealed class ContactInfo
    {
        public string Street { get; set; } = string.Empty;

        [DisableAuditing]
        public string Secret { get; set; } = string.Empty;
    }

    private sealed class NavigationRoot : IEntity<Guid>
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public Guid? ChildId { get; set; }

        public NavigationChild? Child { get; set; }

        public object?[] GetKeys() => [Id];
    }

    private sealed class NavigationChild : IEntity<Guid>
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public object?[] GetKeys() => [Id];
    }

    private sealed class FakeNavigationHelper : SharedEfCoreNavigationHelper
    {
        private readonly SharedNavigationEntry _navigationEntry;

        public FakeNavigationHelper(SharedNavigationEntry navigationEntry)
        {
            _navigationEntry = navigationEntry;
        }

        public override bool IsEntityEntryModified(EntityEntry entityEntry)
        {
            return true;
        }

        public override bool IsNavigationEntryModified(EntityEntry entityEntry, int? navigationEntryIndex = null)
        {
            return navigationEntryIndex == 0;
        }

        public override SharedNavigationEntry? GetNavigationEntry(EntityEntry entityEntry, int navigationEntryIndex)
        {
            return navigationEntryIndex == 0 ? _navigationEntry : null;
        }
    }
}
