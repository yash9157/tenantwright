using System.Threading.Tasks;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Shared.Modularity.DependencyInjection;

namespace Shared.Data.ConnectionStrings;

public class DefaultConnectionStringChecker : IConnectionStringChecker, ITransientDependency
{
    private readonly IEnumerable<IConnectionStringProbe> _probes;
    private readonly ILogger<DefaultConnectionStringChecker> _logger;

    public DefaultConnectionStringChecker(
        IEnumerable<IConnectionStringProbe> probes,
        ILogger<DefaultConnectionStringChecker> logger)
    {
        _probes = probes;
        _logger = logger;
    }

    public Task<SharedConnectionStringCheckResult> CheckAsync(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Task.FromResult(new SharedConnectionStringCheckResult
            {
                IsValid = false,
                Connected = false,
                DatabaseExists = false
            });
        }

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Connection string checker rejected an invalid connection string format.");

            return Task.FromResult(new SharedConnectionStringCheckResult
            {
                IsValid = false,
                Connected = false,
                DatabaseExists = false
            });
        }

        var probe = _probes.FirstOrDefault(candidate => candidate.CanProbe(builder));
        if (probe != null)
        {
            return probe.ProbeAsync(connectionString);
        }

        return Task.FromResult(new SharedConnectionStringCheckResult
        {
            IsValid = true,
            Connected = false,
            DatabaseExists = false
        });
    }
}
