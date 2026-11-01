using System.Data.Common;

namespace Shared.Data.ConnectionStrings;

public interface IConnectionStringProbe
{
    bool CanProbe(DbConnectionStringBuilder builder);

    Task<SharedConnectionStringCheckResult> ProbeAsync(string connectionString, CancellationToken cancellationToken = default);
}
