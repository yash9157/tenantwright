using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.ProviderConfig;
using Notifications.Application.ProviderConfig.Security;
using Notifications.Application.Providers;
using Notifications.Controllers.NotificationProviders;
using Notifications.Domain.Notifications;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Permissions.Application.Contracts.Features;
using Shared.AuditLogs.Auditing;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.Permissions.Definitions.Notifications;
using Xunit;

namespace Notifications.Tests;

public class NotificationProviderLifecycleTests
{
    [Fact]
    public async Task Rotation_rewraps_bounded_stale_batches_and_reports_remaining_progress()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Rotation_rewraps_bounded_stale_batches_and_reports_remaining_progress),
            tenantId: null,
            multiTenantFilterEnabled: false);
        var first = NewConfig(Guid.Parse("00000000-0000-0000-0000-000000000001"), tenantId);
        first.SetSecret("old:first", "first", "old-key");
        var second = NewConfig(Guid.Parse("00000000-0000-0000-0000-000000000002"), tenantId);
        second.SetSecret("old:second", "second", "old-key");
        var current = NewConfig(Guid.Parse("00000000-0000-0000-0000-000000000003"), tenantId);
        current.SetSecret("new:current", "current", "new-key");
        dbContext.NotificationProviderConfigs.AddRange(first, second, current);
        await dbContext.SaveChangesAsync();

        var cache = new RecordingCache();
        var service = new NotificationsProviderSecretRewrapService(
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            new ImmediateUnitOfWorkExecutor(),
            new TestSecretProtector(),
            Options.Create(new NotificationsProviderSecretProtectionOptions { CurrentKeyId = "new-key" }),
            new ImmediateDistributedLock(),
            cache,
            NullLogger<NotificationsProviderSecretRewrapService>.Instance);

        var firstBatch = await service.RewrapBatchAsync(1);
        var secondBatch = await service.RewrapBatchAsync(1);

        Assert.Equal(3, firstBatch.TotalSecretCount);
        Assert.Equal(1, firstBatch.RewrappedCount);
        Assert.Equal(1, firstBatch.RemainingCount);
        Assert.Equal(1, secondBatch.RewrappedCount);
        Assert.Equal(0, secondBatch.RemainingCount);
        Assert.All(dbContext.NotificationProviderConfigs, item => Assert.Equal("new-key", item.SecretProtectionKeyId));
        Assert.Equal(2, cache.RemovedKeys.Count);
    }

    [Fact]
    public async Task Rotation_status_is_tenant_scoped_and_compares_persisted_key_ids()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Rotation_status_is_tenant_scoped_and_compares_persisted_key_ids),
            tenantId: null,
            multiTenantFilterEnabled: false);
        var current = NewConfig(Guid.NewGuid(), tenantId);
        current.SetSecret("new:current", "current", "new-key");
        var stale = NewConfig(Guid.NewGuid(), tenantId, name: "Secondary", isDefault: false);
        stale.SetSecret("old:stale", "stale", "old-key");
        var other = NewConfig(Guid.NewGuid(), otherTenantId);
        other.SetSecret("old:other", "other", "old-key");
        dbContext.NotificationProviderConfigs.AddRange(current, stale, other);
        await dbContext.SaveChangesAsync();

        var service = CreateDiagnostics(dbContext, currentTenantId: null);
        var status = await service.GetSecretRotationStatusAsync(tenantId);

        Assert.Equal(tenantId, status.TenantId);
        Assert.True(status.ProtectionConfigured);
        Assert.Equal(2, status.TotalSecretCount);
        Assert.Equal(1, status.CurrentKeySecretCount);
        Assert.Equal(1, status.RemainingSecretCount);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public async Task Controlled_live_verification_requires_successful_test_and_exact_tenant_scope()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Controlled_live_verification_requires_successful_test_and_exact_tenant_scope),
            tenantId: null,
            multiTenantFilterEnabled: false);
        var config = NewConfig(Guid.NewGuid(), tenantId);
        dbContext.NotificationProviderConfigs.Add(config);
        await dbContext.SaveChangesAsync();

        var hostService = CreateDiagnostics(dbContext, currentTenantId: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hostService.RecordLiveVerificationAsync(config.Id, new RecordNotificationProviderLiveVerificationInput
            {
                TenantId = tenantId,
                EvidenceReference = "verification/run-1"
            }));

        config.RecordTestResult(DateTime.UtcNow.AddMinutes(-1), "Success");
        await dbContext.SaveChangesAsync();
        var verified = await hostService.RecordLiveVerificationAsync(
            config.Id,
            new RecordNotificationProviderLiveVerificationInput
            {
                TenantId = tenantId,
                EvidenceReference = "verification/run-1"
            });

        Assert.Equal(NotificationProviderVerificationState.LiveVerified, verified.VerificationState);
        Assert.Equal("verification/run-1", verified.EvidenceReference);
        Assert.Equal(tenantId, verified.TenantId);

        var foreignTenantService = CreateDiagnostics(dbContext, currentTenantId: otherTenantId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            foreignTenantService.RecordLiveVerificationAsync(config.Id, new RecordNotificationProviderLiveVerificationInput
            {
                TenantId = tenantId,
                EvidenceReference = "verification/forbidden"
            }));

        config.RecordTestResult(DateTime.UtcNow, "Failed");
        Assert.Equal(NotificationProviderVerificationState.VerificationFailed, config.VerificationState);
        Assert.Null(config.LiveVerifiedAtUtc);
        Assert.Null(config.LiveVerificationReference);
    }

    [Fact]
    public void Live_verification_endpoint_is_audited_and_uses_existing_test_send_capability()
    {
        var action = typeof(NotificationProvidersController)
            .GetMethod(nameof(NotificationProvidersController.RecordLiveVerificationAsync));

        Assert.NotNull(action);
        Assert.Equal(
            "{configurationId:guid}/live-verification",
            action!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Config.TestSend,
            action.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.NotNull(action.GetCustomAttribute<AuditedAttribute>());
    }

    private static NotificationProviderDiagnosticsAppService CreateDiagnostics(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp dbContext,
        Guid? currentTenantId)
    {
        return new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new AlwaysEnabledFeatureChecker(),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(currentTenantId),
            SharedOnlyTenantDatabases.FanOut(),
            Options.Create(new NotificationsProviderSecretProtectionOptions { CurrentKeyId = "new-key" }));
    }

    private static NotificationProviderConfig NewConfig(
        Guid id,
        Guid tenantId,
        string name = "Primary",
        bool isDefault = true)
    {
        return new NotificationProviderConfig(
            id,
            tenantId,
            NotificationChannel.Sms,
            "Twilio",
            name,
            isActive: true,
            isDefault,
            priority: 0,
            settingsJson: "{}",
            complianceContact: "Platform Operations");
    }

    private sealed class AlwaysEnabledFeatureChecker : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(
            Guid? tenantId,
            IReadOnlyCollection<string> featureNames,
            CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(x => x, _ => true, StringComparer.Ordinal));
    }

    private sealed class TestSecretProtector : INotificationsProviderSecretProtector
    {
        public string Protect(Guid? tenantId, NotificationChannel channel, Guid configId, string plaintext)
            => $"new:{plaintext}";

        public string Unprotect(Guid? tenantId, NotificationChannel channel, Guid configId, string storedValue)
            => storedValue[(storedValue.IndexOf(':', StringComparison.Ordinal) + 1)..];

        public bool IsProtected(string? storedValue) => !string.IsNullOrWhiteSpace(storedValue);

        public bool IsProtectedByCurrentKey(string? storedValue)
            => storedValue?.StartsWith("new:", StringComparison.Ordinal) == true;

        public string Fingerprint(string plaintext) => $"fingerprint-{plaintext}";
    }

    private sealed class ImmediateDistributedLock : IDistributedLock
    {
        public Task<IDistributedLockHandle?> TryAcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IDistributedLockHandle?>(new Handle(name));

        public Task<IDistributedLockHandle> AcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IDistributedLockHandle>(new Handle(name));

        private sealed class Handle(string name) : IDistributedLockHandle
        {
            public string Name { get; } = name;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCache : IHybridCache<NotificationProviderConfigCacheItem, string>
    {
        public List<string> RemovedKeys { get; } = [];

        public Task<NotificationProviderConfigCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<NotificationProviderConfigCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
            => factory()!;

        public Task SetAsync(
            string key,
            NotificationProviderConfigCacheItem value,
            HybridCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
            => Task.CompletedTask;

        public Task RemoveAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            RemovedKeys.Add(key);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            RemovedKeys.AddRange(keys);
            return Task.CompletedTask;
        }
    }
}
