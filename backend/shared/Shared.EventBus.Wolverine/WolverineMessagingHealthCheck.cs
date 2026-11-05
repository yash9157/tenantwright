using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.EventBus.Messaging;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// Readiness of the Wolverine provider: the Wolverine RabbitMQ connection is up and not blocked, every catalog receive
/// endpoint has a listener accepting deliveries, the confirmed publisher connection is open, and no terminal
/// publication is blocked. Anything else is
/// unhealthy, so a host that cannot consume or dispatch is taken out of rotation. Readiness only, never liveness.
/// </summary>
internal sealed class WolverineMessagingHealthCheck(
    IWolverineRuntime runtime,
    TenantWrightMessageCatalog catalog,
    RabbitMqConfirmedPublisher publisher,
    WolverineTerminalPublisher terminalPublisher) : IHealthCheck
{
    public const string Name = "tenantwright-wolverine";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (terminalPublisher.HasBlockedPublications)
            return HealthCheckResult.Unhealthy("Signed terminal publication is blocked; original deliveries remain unacknowledged.");

        var transport = runtime.Options.Transports.OfType<RabbitMqTransport>().SingleOrDefault();
        if (transport is null)
            return HealthCheckResult.Unhealthy("The Wolverine RabbitMQ transport is not configured.");

        var broker = await transport.BuildHealthCheck(runtime).CheckHealthAsync(cancellationToken);
        if (broker.Status != TransportHealthStatus.Healthy)
            return HealthCheckResult.Unhealthy(broker.Message ?? "The Wolverine RabbitMQ connection is not healthy.");

        foreach (var endpoint in catalog.ReceiveEndpoints)
        {
            var listener = runtime.Endpoints.FindListeningAgent(endpoint.Name);
            if (listener?.Status != ListeningStatus.Accepting)
                return HealthCheckResult.Unhealthy(
                    $"Receive endpoint {endpoint.Name} is not accepting deliveries ({listener?.Status.ToString() ?? "no listener"}).");
        }

        try
        {
            await publisher.EnsureConnectedAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Exception type only: broker exception text can echo connection details.
            return HealthCheckResult.Unhealthy(
                $"The RabbitMQ publisher connection is unavailable ({exception.GetType().Name}).");
        }

        return HealthCheckResult.Healthy();
    }
}
