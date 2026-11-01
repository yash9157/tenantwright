using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Uow.Options;

namespace AuditLogs.Tests.TestDoubles;

/// <summary>
/// Minimal harness for building a real <see cref="AuditLogsDbContextApp"/> over SQLite (relational),
/// needed for tests that exercise <c>AuditLogSealService</c>/<c>AuditLogLegalHoldService</c> logic that
/// relies on relational query translation (e.g. keyset ordering, ExecuteDelete) not faithfully
/// reproduced by the EF Core InMemory provider. Mirrors the Permissions.Tests harness pattern.
/// </summary>
internal static class AuditLogsEfCoreTestHarness
{
    public static AuditLogsDbContextApp BuildSqliteDbContext(SqliteConnection connection, bool multiTenantFilterEnabled = false)
    {
        var options = new DbContextOptionsBuilder<AuditLogsDbContextApp>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new AuditLogsDbContextApp(options)
        {
            ServiceProvider = CreateServiceProvider(multiTenantFilterEnabled)
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    private static ServiceProvider CreateServiceProvider(bool multiTenantFilterEnabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant(null));
        services.AddSingleton<IDataFilter>(new ConfigurableDataFilter(multiTenantFilterEnabled));
        services.AddSingleton<IAuditPropertySetter>(new NoopAuditPropertySetter());
        services.AddSingleton<IAuditingManager>(new NoopAuditingManager());
        services.AddSingleton<Shared.Uow.Abstractions.IUnitOfWorkManager>(new NoopUnitOfWorkManager());
        return services.BuildServiceProvider();
    }
}

internal sealed class DisposeAction : IDisposable
{
    private readonly Action _action;
    public DisposeAction(Action action) => _action = action;
    public void Dispose() => _action();
}

internal sealed class TestCurrentTenant : ICurrentTenant
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
        return new DisposeAction(() =>
        {
            Id = previousId;
            Name = previousName;
        });
    }
}

internal sealed class ConfigurableDataFilter : IDataFilter
{
    private readonly bool _enabled;

    public ConfigurableDataFilter(bool enabled)
    {
        _enabled = enabled;
    }

    public IDisposable Enable<TFilter>() where TFilter : class => new DisposeAction(() => { });
    public IDisposable Disable<TFilter>() where TFilter : class => new DisposeAction(() => { });
    public bool IsEnabled<TFilter>() where TFilter : class => _enabled;
}

internal sealed class NoopAuditPropertySetter : IAuditPropertySetter
{
    public void SetCreationProperties(object entity) { }
    public void SetModificationProperties(object entity) { }
    public void SetDeletionProperties(object entity) { }
    public void IncrementEntityVersionProperty(object entity) { }
}

internal sealed class NoopAuditingManager : IAuditingManager
{
    public IAuditLogScope? Current { get; } = new NoopAuditLogScope();

    public IAuditLogSaveHandle BeginScope()
    {
        return NoopAuditLogSaveHandle.Instance;
    }
}

internal sealed class NoopAuditLogScope : IAuditLogScope
{
    public AuditLogInfo Log { get; } = new();
}

internal sealed class NoopAuditLogSaveHandle : IAuditLogSaveHandle
{
    public static readonly NoopAuditLogSaveHandle Instance = new();

    public Task SaveAsync() => Task.CompletedTask;

    public void Dispose()
    {
    }
}
