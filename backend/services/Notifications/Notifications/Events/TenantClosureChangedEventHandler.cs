using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Notifications;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Events;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantClosureChangedEto>),
    IncludeSelf = true)]
public sealed class TenantClosureChangedEventHandler
    : ITenantWrightMessageHandler<TenantClosureChangedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;

    public TenantClosureChangedEventHandler(IDbContextProvider<NotificationsDbContextApp> dbContextProvider)
        => _dbContextProvider = dbContextProvider;

    public async Task HandleAsync(
        TenantClosureChangedEto eventData,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var row = await dbContext.TenantNotificationSuppressions
            .FirstOrDefaultAsync(
                suppression => suppression.TenantId == eventData.TenantId,
                cancellationToken);
        if (row == null)
        {
            row = new TenantNotificationSuppression { TenantId = eventData.TenantId };
            await dbContext.AddAsync(row, cancellationToken);
        }

        row.IsSuppressed = eventData.IsClosed;
        row.UpdatedAtUtc = eventData.OccurredAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
