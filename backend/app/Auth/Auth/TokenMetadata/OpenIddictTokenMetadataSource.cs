using OpenIddict.Abstractions;
using Shared.Modularity.DependencyInjection;

namespace Auth.TokenMetadata;

public sealed class OpenIddictTokenMetadataSource : IOpenIddictTokenMetadataSource, ITransientDependency
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;

    public OpenIddictTokenMetadataSource(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager)
    {
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
    }

    public async Task<TokenClientMetadataCacheItem?> GetClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var application = await _applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application == null)
        {
            return null;
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);

        return new TokenClientMetadataCacheItem
        {
            ClientId = descriptor.ClientId ?? clientId,
            DisplayName = descriptor.DisplayName ?? string.Empty,
            ClientType = descriptor.ClientType ?? string.Empty,
            ConsentType = descriptor.ConsentType ?? string.Empty,
            Permissions = descriptor.Permissions
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            RedirectUris = descriptor.RedirectUris
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            PostLogoutRedirectUris = descriptor.PostLogoutRedirectUris
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    public async Task<string[]> GetScopeResourcesAsync(string scopeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            return [];
        }

        var scope = await _scopeManager.FindByNameAsync(scopeName, cancellationToken);
        if (scope == null)
        {
            return [];
        }

        var descriptor = new OpenIddictScopeDescriptor();
        await _scopeManager.PopulateAsync(descriptor, scope, cancellationToken);

        return descriptor.Resources
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async IAsyncEnumerable<string> ListClientIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var application in _applicationManager.ListAsync(cancellationToken: cancellationToken))
        {
            var clientId = await _applicationManager.GetClientIdAsync(application, cancellationToken);
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                yield return clientId;
            }
        }
    }

    public async IAsyncEnumerable<string> ListScopeNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var scope in _scopeManager.ListAsync(cancellationToken: cancellationToken))
        {
            var name = await _scopeManager.GetNameAsync(scope, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }
}
