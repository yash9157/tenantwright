using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.EntityFrameworkCore.ConnectionStrings;

public interface ITenantConnectionStringsCache
{
    Task<TenantConnectionStringsCacheItem> GetOrAddAsync(
        Guid tenantId,
        Func<Task<TenantConnectionStringsCacheItem>> factory,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
