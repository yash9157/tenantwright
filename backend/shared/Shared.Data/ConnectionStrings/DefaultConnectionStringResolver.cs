using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Shared.Modularity.DependencyInjection;

namespace Shared.Data.ConnectionStrings;

public class DefaultConnectionStringResolver : IConnectionStringResolver, ITransientDependency
{
    protected IOptionsMonitor<SharedDbConnectionOptions> ConnectionOptions { get; }
    protected IOptionsMonitor<SharedConnectionStringResolverOptions> ResolverOptions { get; }
    protected ILogger<DefaultConnectionStringResolver> Logger { get; }

    public DefaultConnectionStringResolver(
        IOptionsMonitor<SharedDbConnectionOptions> options,
        IOptionsMonitor<SharedConnectionStringResolverOptions> resolverOptions,
        ILogger<DefaultConnectionStringResolver> logger)
    {
        ConnectionOptions = options;
        ResolverOptions = resolverOptions;
        Logger = logger;
    }

    public virtual Task<string> ResolveAsync(string? connectionStringName = null)
    {
        return Task.FromResult(ResolveInternal(connectionStringName));
    }

    private string ResolveInternal(string? connectionStringName)
    {
        var options = ConnectionOptions.CurrentValue;

        if (connectionStringName == null)
        {
            return EnsureResolved(options.ConnectionStrings.Default, null);
        }

        var connectionString = options.GetConnectionStringOrNull(connectionStringName);

        if (!string.IsNullOrEmpty(connectionString))
        {
            return connectionString;
        }

        return EnsureResolved(null, connectionStringName);
    }

    private string EnsureResolved(string? connectionString, string? connectionStringName)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        if (!ResolverOptions.CurrentValue.ThrowOnMissing)
        {
            return string.Empty;
        }

        ConnectionStringResolutionMetrics.RecordFailure(nameof(DefaultConnectionStringResolver), connectionStringName);
        Logger.LogError(
            "Connection string resolution failed in {Resolver} for connection name '{ConnectionName}'.",
            nameof(DefaultConnectionStringResolver),
            connectionStringName ?? ConnectionStrings.DefaultConnectionStringName);

        throw new InvalidOperationException(
            $"Connection string '{connectionStringName ?? ConnectionStrings.DefaultConnectionStringName}' could not be resolved.");
    }
}
