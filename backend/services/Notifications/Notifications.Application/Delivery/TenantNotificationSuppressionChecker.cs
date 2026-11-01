using Microsoft.EntityFrameworkCore;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

public interface ITenantNotificationSuppressionChecker
{
    Task<bool> IsSuppressedAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class TenantNotificationSuppressionChecker : ITenantNotificationSuppressionChecker, ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;

    public TenantNotificationSuppressionChecker(IDbContextProvider<NotificationsDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<bool> IsSuppressedAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await dbContext.TenantNotificationSuppressions
            .IgnoreQueryFilters()
            .AnyAsync(
                suppression => suppression.TenantId == tenantId && suppression.IsSuppressed,
                cancellationToken);
    }
}
