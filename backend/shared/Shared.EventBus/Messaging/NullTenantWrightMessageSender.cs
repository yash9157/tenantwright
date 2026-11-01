using Microsoft.Extensions.Logging;

namespace Shared.EventBus.Messaging;

/// <summary>
/// The <see cref="ITenantWrightMessageSender"/> of a bus-free host (<c>Messaging:Provider=None</c>). Commands imply a
/// required consumer and fail closed instead of being silently dropped. Events are best-effort notifications with
/// nothing to notify on a bus-free deployment, so they are dropped with a Debug log and a
/// <c>tenantwright.messaging.event.dropped</c> count.
/// </summary>
/// <remarks>
/// Audit is not a command callers should see fail here: under <c>None</c>, <c>Shared.AuditLogs</c> routes every audit
/// record to its structured log store and never sends the audit write command.
/// </remarks>
public sealed class NullTenantWrightMessageSender(ILogger<NullTenantWrightMessageSender> logger) : ITenantWrightMessageSender
{
    public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(command);
        throw new InvalidOperationException(
            $"Cannot send command '{typeof(T).Name}': {TenantWrightMessagingProviderConfiguration.ProviderKey} is None " +
            "on this workload, so no consumer will ever process it.");
    }

    public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        TenantWrightMessagingMetrics.RecordEventDropped(typeof(T).FullName ?? typeof(T).Name);
        logger.LogDebug(
            "Dropped event '{EventType}': messaging provider is None on this workload.",
            typeof(T).Name);
        return Task.CompletedTask;
    }
}
