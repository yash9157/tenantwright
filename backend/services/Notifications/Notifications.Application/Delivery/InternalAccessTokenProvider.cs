using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

/// <summary>
/// Notifications' outbound M2M token acquisition. Every caller names its client profile explicitly; Notifications has
/// no implicit default identity, so a missing profile fails closed instead of borrowing another credential.
/// </summary>
public sealed class InternalAccessTokenProvider(Shared.Permissions.Features.IInternalAccessTokenProvider inner) : ITransientDependency
{
    public Task<string?> GetAccessTokenForClientAsync(
        string clientProfileName,
        string scope,
        CancellationToken cancellationToken = default) =>
        inner.GetAccessTokenForClientAsync(clientProfileName, scope, cancellationToken);
}
