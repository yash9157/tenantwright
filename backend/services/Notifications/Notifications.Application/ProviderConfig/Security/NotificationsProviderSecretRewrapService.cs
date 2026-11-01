using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.Uow.Abstractions;
namespace Notifications.Application.ProviderConfig.Security;

/// <summary>Explicitly re-encrypts provider secrets under the current configured key.</summary>
public sealed class NotificationsProviderSecretRewrapService : ITransientDependency
{
    private const string LockName = "notifications:provider-secrets:key-rewrap";
    private static readonly DistributedLockAcquireOptions LockOptions = new()
    {
        AcquireTimeoutMs = 30000,
        LeaseMs = 300000,
        FailureMode = DistributedLockFailureMode.FailClosed,
        IgnoreTenant = true
    };

    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly INotificationsProviderSecretProtector _protector;
    private readonly IOptions<NotificationsProviderSecretProtectionOptions> _options;
    private readonly IDistributedLock _distributedLock;
    private readonly IHybridCache<NotificationProviderConfigCacheItem, string> _cache;
    private readonly ILogger<NotificationsProviderSecretRewrapService> _logger;

    public NotificationsProviderSecretRewrapService(
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        INotificationsProviderSecretProtector protector,
        IOptions<NotificationsProviderSecretProtectionOptions> options,
        IDistributedLock distributedLock,
        IHybridCache<NotificationProviderConfigCacheItem, string> cache,
        ILogger<NotificationsProviderSecretRewrapService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _protector = protector;
        _options = options;
        _distributedLock = distributedLock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<int> RewrapAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await RewrapBatchAsync(500, cancellationToken);
            total += result.RewrappedCount;
            if (result.RemainingCount == 0 || result.RewrappedCount == 0)
            {
                return total;
            }
        }

        return total;
    }

    public async Task<ProviderSecretRotationBatchResult> RewrapBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.CurrentKeyId))
        {
            throw new InvalidOperationException("Notifications:ProviderSecrets:Protection:CurrentKeyId is required for key rewrap.");
        }

        await using var handle = await _distributedLock.AcquireAsync(LockName, LockOptions, cancellationToken);
        return await _unitOfWorkExecutor.ExecuteTransactionalAsync(
            _ => RewrapBatchCoreAsync(batchSize, cancellationToken),
            cancellationToken);
    }

    private async Task<ProviderSecretRotationBatchResult> RewrapBatchCoreAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var currentKeyId = _options.Value.CurrentKeyId;
        var candidates = await unitOfWorkDbContext.NotificationProviderConfigs
            .IgnoreQueryFilters()
            .Where(x => x.SecretEnvelope != null && x.SecretProtectionKeyId != currentKeyId)
            .OrderBy(x => x.Id)
            .Take(Math.Clamp(batchSize, 1, 1000))
            .ToListAsync(cancellationToken);
        var rewrapped = 0;
        foreach (var row in candidates)
        {
            var plaintext = _protector.Unprotect(row.TenantId, row.Channel, row.Id, row.SecretEnvelope!);
            row.SetSecret(
                _protector.Protect(row.TenantId, row.Channel, row.Id, plaintext),
                _protector.Fingerprint(plaintext),
                currentKeyId);
            await _cache.RemoveAsync(
                NotificationProviderConfigCacheKeys.Default(row.TenantId, row.Channel),
                considerUow: false,
                token: cancellationToken);
            rewrapped++;
        }

        if (rewrapped > 0)
        {
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Re-wrapped {ProviderSecretCount} Notifications provider secrets under the current protection key.", rewrapped);
        }

        var totalCount = await unitOfWorkDbContext.NotificationProviderConfigs
            .IgnoreQueryFilters()
            .CountAsync(x => x.SecretEnvelope != null, cancellationToken);
        var remaining = await unitOfWorkDbContext.NotificationProviderConfigs
            .IgnoreQueryFilters()
            .CountAsync(
                x => x.SecretEnvelope != null && x.SecretProtectionKeyId != currentKeyId,
                cancellationToken);
        return new ProviderSecretRotationBatchResult(totalCount, rewrapped, remaining);
    }
}

public sealed record ProviderSecretRotationBatchResult(
    int TotalSecretCount,
    int RewrappedCount,
    int RemainingCount);
