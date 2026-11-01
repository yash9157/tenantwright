using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Shared.EventBus.Wolverine;

namespace Shared.EventBus.Hosting;

/// <summary>
/// The transport providers compiled into this build. <see cref="TenantWrightMessagingProvider.None"/> is not a provider:
/// it is the absence of one and is composed by <see cref="TenantWrightMessagingHostExtensions"/> directly.
/// </summary>
internal static class TenantWrightMessagingProviders
{
    private static readonly IReadOnlyList<ITenantWrightMessagingProvider> Installed =
    [
        new MassTransitMessagingProvider(),
        new WolverineMessagingProvider()
    ];

    /// <summary>The one installed provider of <paramref name="kind"/>; fails closed when none is installed.</summary>
    public static ITenantWrightMessagingProvider Resolve(TenantWrightMessagingProvider kind) =>
        Installed.SingleOrDefault(provider => provider.Kind == kind)
        ?? throw new InvalidOperationException(
            $"{TenantWrightMessagingProviderConfiguration.ProviderKey}={kind}: the {kind} messaging provider is not " +
            $"installed in this build. Installed providers: {string.Join(", ", Installed.Select(provider => provider.Kind))}; " +
            $"use {TenantWrightMessagingProvider.None} for a bus-free host.");
}
