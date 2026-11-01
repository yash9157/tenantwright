using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Data.ConnectionStrings;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>The connection a DbContext must use in the current tenant scope.</summary>
/// <param name="IsTenantDedicated">
/// True only when the current tenant resolves to its own dedicated database (a value different from the host
/// resolution of the same name). Host scope, host-only names and shared-database tenants are never dedicated.
/// </param>
public sealed record TenantDatabaseConnection(
    string ConnectionStringName,
    string ConnectionString,
    bool IsTenantDedicated);

/// <summary>
/// Binds service DbContexts to the database the current tenant scope resolves to.
/// <para>
/// Service DbContexts are registered with the host <c>ConnectionStrings:Default</c> and are scoped, so the DI
/// instance can only ever serve the shared database. A dedicated tenant database therefore always gets a separate,
/// caller-owned instance whose connection string is replaced before first use; the shared scoped instance is never
/// re-pointed, so work that changes tenant scope mid-scope can never move another caller's context to a different
/// database.
/// </para>
/// </summary>
public static class TenantDatabaseDbContextActivator
{
    public static async Task<TenantDatabaseConnection> ResolveConnectionAsync(
        Type dbContextType,
        IConnectionStringResolver resolver,
        ICurrentTenant currentTenant)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(currentTenant);

        var connectionStringName = ConnectionStringNameAttribute.GetConnStringName(dbContextType);
        if (dbContextType.IsDefined(typeof(IgnoreMultiTenancyAttribute), inherit: false))
        {
            using (currentTenant.Change(null))
            {
                return new TenantDatabaseConnection(
                    connectionStringName,
                    await resolver.ResolveAsync(connectionStringName),
                    IsTenantDedicated: false);
            }
        }

        var connectionString = await resolver.ResolveAsync(connectionStringName);
        if (currentTenant.Id is not { } tenantId || tenantId == Guid.Empty ||
            !TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(connectionStringName))
        {
            return new TenantDatabaseConnection(connectionStringName, connectionString, IsTenantDedicated: false);
        }

        string hostConnectionString;
        using (currentTenant.Change(null))
        {
            hostConnectionString = await resolver.ResolveAsync(connectionStringName);
        }

        return new TenantDatabaseConnection(
            connectionStringName,
            connectionString,
            IsTenantDedicated: !string.Equals(connectionString, hostConnectionString, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the scoped DI instance for a non-dedicated connection, otherwise a new instance bound to the dedicated
    /// connection string. <paramref name="owned"/> tells the caller it must dispose the returned instance.
    /// </summary>
    public static TDbContext Activate<TDbContext>(
        IServiceProvider serviceProvider,
        TenantDatabaseConnection connection,
        out bool owned)
        where TDbContext : class =>
        (TDbContext)Activate(serviceProvider, typeof(TDbContext), connection, out owned);

    /// <inheritdoc cref="Activate{TDbContext}"/>
    public static object Activate(
        IServiceProvider serviceProvider,
        Type dbContextType,
        TenantDatabaseConnection connection,
        out bool owned)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(dbContextType);
        ArgumentNullException.ThrowIfNull(connection);

        if (!connection.IsTenantDedicated)
        {
            owned = false;
            return serviceProvider.GetRequiredService(dbContextType);
        }

        var dedicated = ActivatorUtilities.CreateInstance(serviceProvider, dbContextType);
        if (dedicated is not DbContext dbContext || !dbContext.Database.IsRelational())
        {
            (dedicated as IDisposable)?.Dispose();
            throw new InvalidOperationException(
                $"Dedicated tenant databases require a relational DbContext; '{dbContextType.Name}' is not one.");
        }

        dbContext.Database.SetConnectionString(connection.ConnectionString);
        owned = true;
        return dedicated;
    }

    /// <summary>
    /// Resolves and activates <typeparamref name="TDbContext"/> for the current tenant scope outside a unit of work
    /// (background loops that drive raw SQL through the context). Dispose the lease when done.
    /// </summary>
    public static async Task<TenantDatabaseDbContextLease<TDbContext>> CreateForCurrentScopeAsync<TDbContext>(
        IServiceProvider serviceProvider)
        where TDbContext : class
    {
        var connection = await ResolveConnectionAsync(
            typeof(TDbContext),
            serviceProvider.GetRequiredService<IConnectionStringResolver>(),
            serviceProvider.GetRequiredService<ICurrentTenant>());
        var dbContext = Activate<TDbContext>(serviceProvider, connection, out var owned);
        return new TenantDatabaseDbContextLease<TDbContext>(dbContext, connection, owned);
    }
}

/// <summary>A DbContext bound to one database; disposes the instance only when the activator created it.</summary>
public sealed class TenantDatabaseDbContextLease<TDbContext> : IAsyncDisposable
    where TDbContext : class
{
    private readonly bool _owned;

    internal TenantDatabaseDbContextLease(TDbContext dbContext, TenantDatabaseConnection connection, bool owned)
    {
        DbContext = dbContext;
        Connection = connection;
        _owned = owned;
    }

    public TDbContext DbContext { get; }

    public TenantDatabaseConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        if (!_owned)
        {
            return;
        }

        if (DbContext is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (DbContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
