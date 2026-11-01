using Notifications.Application.Contracts.Realtime;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Realtime;

[DisableConventionalRegistration]
public sealed class NoopNotificationsRealtimePublisher : INotificationsRealtimePublisher, ITransientDependency
{
    public Task PublishAppNotificationAsync(AppNotificationSignalDto input, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishSessionRevokedAsync(SessionRevokedSignalDto input, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
