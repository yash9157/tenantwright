using Shared.EventBus.Messaging;
using Wolverine;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// Runs <see cref="TenantWrightMessagingProviderPreconditions"/> when the Wolverine runtime starts, after options
/// validation and before any RabbitMQ connection, queue declaration or listener exists, so a misconfigured host fails
/// to start instead of consuming.
/// </summary>
internal sealed class WolverineStartupVerification(IServiceProvider services, TenantWrightMessageCatalog catalog)
    : IAsyncWolverineExtension
{
    public ValueTask Configure(WolverineOptions options)
    {
        TenantWrightMessagingProviderPreconditions.Verify(services, catalog, TenantWrightMessagingProvider.Wolverine);
        return ValueTask.CompletedTask;
    }
}
