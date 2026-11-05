using Microsoft.Extensions.Diagnostics.HealthChecks;
using Notifications.Application.Dispatches;

namespace Notifications.Delivery;

public sealed class NotificationDispatchReadinessHealthCheck(NotificationDispatchHealthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsReady ? HealthCheckResult.Healthy() :
            HealthCheckResult.Unhealthy("Notification reconciliation has not completed recently or its last cycle failed."));
}
