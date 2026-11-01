using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.Contracts.SecuritySettings;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Shared.Caching.Abstractions;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.SecuritySettings;

[ExposeServices(typeof(IHostIdentitySecurityPolicyProvider))]
public sealed class HostIdentitySecurityPolicyProvider : IHostIdentitySecurityPolicyProvider, ITransientDependency
{
    private const string CacheKey = "identity:security-settings:host";

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(60)
    };

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly IHybridCache<HostIdentitySecuritySettingsDto, string> _cache;
    private readonly HostIdentitySecurityStaticOptions _staticOptions;
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;

    public HostIdentitySecurityPolicyProvider(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, IHybridCache<HostIdentitySecuritySettingsDto, string> cache, HostIdentitySecurityStaticOptions staticOptions, HostIdentitySecurityPolicyRuntimeState runtimeState)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _cache = cache;
        _staticOptions = staticOptions;
        _runtimeState = runtimeState;
    }

    public async Task<HostIdentitySecuritySettingsDto> GetEffectiveAsync(CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        using var _ = _currentTenant.Change(null);

        var cached = await _cache.GetOrCreateAsync(
            CacheKey,
            async () =>
            {
                var policy = await unitOfWorkDbContext.HostIdentitySecurityPolicies
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == HostIdentitySecurityPolicy.SingletonId, cancellationToken);

                return policy == null
                    ? HostIdentitySecuritySettingsMapper.Clone(_staticOptions.DefaultSettings)
                    : HostIdentitySecuritySettingsMapper.FromPolicy(policy);
            },
            () => CacheOptions,
            token: cancellationToken);

        var effective = cached == null
            ? HostIdentitySecuritySettingsMapper.Clone(_staticOptions.DefaultSettings)
            : HostIdentitySecuritySettingsMapper.Clone(cached);

        _runtimeState.SetCurrent(effective);
        return effective;
    }

    public Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        return _cache.RemoveAsync(CacheKey, token: cancellationToken);
    }
}
