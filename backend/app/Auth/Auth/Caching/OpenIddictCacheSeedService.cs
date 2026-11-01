using Auth.Managers;
using Auth.TokenMetadata;
using Microsoft.Extensions.Logging;
using Shared.Modularity.DependencyInjection;

namespace Auth.Caching;

public interface IOpenIddictCacheSeedService
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

public sealed class OpenIddictCacheSeedService : IOpenIddictCacheSeedService, ITransientDependency
{
    private const int PageSize = 100;

    private static readonly IReadOnlyList<string[]> CommonTokenScopeSets = new[]
    {
        new[] { "features.check" },
        new[] { "permissions.emergency-access.validate" },
        new[] { "identity-internal" },
        new[] { "tenant-internal" },
        new[] { "openid", "profile", "roles", "identity", "tenant", "permissions", "notifications", "auditlogs", "workflows", "offline_access" }
    };

    private readonly OpenIddictManagementManager _managementManager;
    private readonly IOpenIddictApplicationCache _applicationCache;
    private readonly IOpenIddictScopeCache _scopeCache;
    private readonly ITokenMetadataWarmupService _tokenMetadataWarmupService;
    private readonly ITokenMetadataStampStore _tokenMetadataStampStore;
    private readonly ILogger<OpenIddictCacheSeedService> _logger;

    public OpenIddictCacheSeedService(
        OpenIddictManagementManager managementManager,
        IOpenIddictApplicationCache applicationCache,
        IOpenIddictScopeCache scopeCache,
        ITokenMetadataWarmupService tokenMetadataWarmupService,
        ITokenMetadataStampStore tokenMetadataStampStore,
        ILogger<OpenIddictCacheSeedService> logger)
    {
        _managementManager = managementManager;
        _applicationCache = applicationCache;
        _scopeCache = scopeCache;
        _tokenMetadataWarmupService = tokenMetadataWarmupService;
        _tokenMetadataStampStore = tokenMetadataStampStore;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var applicationCount = await SeedApplicationsAsync(cancellationToken);
        var scopeCount = await SeedScopesAsync(cancellationToken);

        _logger.LogInformation(
            "OpenIddict cache warm-up completed. Applications={ApplicationCount}, Scopes={ScopeCount}",
            applicationCount,
            scopeCount);

        // Startup seeding rewrites the configuration-managed clients and scopes directly through the
        // OpenIddict managers, which does not advance the token-metadata stamp. Advancing it here makes
        // the warm-up rebuild scope resources from the store instead of reusing entries cached by an
        // earlier process against older (or, on a fresh database, absent) scopes.
        await _tokenMetadataStampStore.BumpStampAsync(cancellationToken);
        await _tokenMetadataWarmupService.WarmupAsync(cancellationToken);
        await _tokenMetadataWarmupService.WarmupCommonScopeSetsAsync(CommonTokenScopeSets, cancellationToken);
    }

    private async Task<int> SeedApplicationsAsync(CancellationToken cancellationToken)
    {
        var skipCount = 0;
        var count = 0;
        do
        {
            var page = await _managementManager.GetApplicationsAsync(
                search: null,
                clientType: null,
                configurationManaged: null,
                skipCount,
                PageSize,
                cancellationToken);
            foreach (var application in page.Items)
            {
                await _applicationCache.SetAsync(application, cancellationToken);
                count++;
            }
            skipCount += page.Items.Count;
            if (!page.HasMore)
            {
                break;
            }
        }
        while (true);

        return count;
    }

    private async Task<int> SeedScopesAsync(CancellationToken cancellationToken)
    {
        var skipCount = 0;
        var count = 0;
        do
        {
            var page = await _managementManager.GetScopesAsync(
                search: null,
                configurationManaged: null,
                skipCount,
                PageSize,
                cancellationToken);
            foreach (var scope in page.Items)
            {
                await _scopeCache.SetAsync(scope, cancellationToken);
                count++;
            }
            skipCount += page.Items.Count;
            if (!page.HasMore)
            {
                break;
            }
        }
        while (true);

        return count;
    }
}
