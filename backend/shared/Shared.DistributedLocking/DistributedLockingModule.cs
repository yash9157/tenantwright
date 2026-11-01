using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Shared.Caching;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Implementations;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Shared.DistributedLocking;

[DependsOn(typeof(CachingModule))]
public class DistributedLockingModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var valkeyConfiguration = ResolveValkeyConfiguration(context.Configuration);
        var provider = DistributedLockingEnvironmentGuard.ResolveProvider(
            context.Configuration[$"{DistributedLockingOptions.SectionName}:Provider"]);

        DistributedLockingEnvironmentGuard.ValidateProvider(provider, valkeyConfiguration);

        context.Services.Configure<DistributedLockingOptions>(context.Configuration.GetSection(DistributedLockingOptions.SectionName));
        context.Services.PostConfigure<DistributedLockingOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.Provider))
            {
                options.Provider = "Valkey";
            }

            options.Valkey.Configuration ??= valkeyConfiguration;

            ApplyPolicyDefaults(options);
        });

        context.Services.TryAddSingleton<IDistributedLockKeyNormalizer, DefaultDistributedLockKeyNormalizer>();
        context.Services.Replace(ServiceDescriptor.Singleton<IDistributedLock, DistributedLockManager>());
    }

    private static string? ResolveValkeyConfiguration(IConfiguration configuration)
    {
        return configuration[$"{DistributedLockingOptions.SectionName}:Valkey:Configuration"]
               ?? configuration["Caching:Valkey:Configuration"];
    }

    private static void ApplyPolicyDefaults(DistributedLockingOptions options)
    {
        Normalize(options.DefaultPolicy, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 1000,
            LeaseMs = 30000,
            FailureMode = DistributedLockFailureMode.FailClosed,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 75
        });

        EnsurePolicy(options, DistributedLockPolicyNames.EfCoreMigration, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 30000,
            LeaseMs = 120000,
            FailureMode = DistributedLockFailureMode.FailClosed,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 100
        });

        EnsurePolicy(options, DistributedLockPolicyNames.EfCoreOutbox, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 250,
            LeaseMs = 4000,
            FailureMode = DistributedLockFailureMode.BestEffort,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 75
        });

        EnsurePolicy(options, DistributedLockPolicyNames.EventBusOutbox, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 500,
            LeaseMs = 8000,
            FailureMode = DistributedLockFailureMode.BestEffort,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 75
        });

        EnsurePolicy(options, DistributedLockPolicyNames.EventBusInbox, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 500,
            LeaseMs = 8000,
            FailureMode = DistributedLockFailureMode.BestEffortWithRetry,
            EnableMetrics = true,
            RetryCount = 1,
            RetryDelayMs = 100
        });

        EnsurePolicy(options, DistributedLockPolicyNames.AuthIdentitySnapshotRefresh, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 300,
            LeaseMs = 5000,
            FailureMode = DistributedLockFailureMode.PerUse,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 75
        });

        EnsurePolicy(options, DistributedLockPolicyNames.M2mTokenAcquisition, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 500,
            LeaseMs = 15000,
            FailureMode = DistributedLockFailureMode.BestEffortWithRetry,
            EnableMetrics = true,
            RetryCount = 2,
            RetryDelayMs = 100
        });

        EnsurePolicy(options, DistributedLockPolicyNames.ApiIdempotency, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 2000,
            LeaseMs = 30000,
            FailureMode = DistributedLockFailureMode.FailClosed,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 100
        });

        EnsurePolicy(options, DistributedLockPolicyNames.TenantListCacheMutation, new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = 5000,
            LeaseMs = 30000,
            FailureMode = DistributedLockFailureMode.FailClosed,
            EnableMetrics = true,
            RetryCount = 0,
            RetryDelayMs = 100
        });

        options.Valkey.BusyWaitMinMs = Math.Max(10, options.Valkey.BusyWaitMinMs);
        options.Valkey.BusyWaitMaxMs = Math.Max(options.Valkey.BusyWaitMinMs, options.Valkey.BusyWaitMaxMs);
    }

    private static void EnsurePolicy(
        DistributedLockingOptions options,
        string policyName,
        DistributedLockPolicyOptions defaults)
    {
        if (!options.Policies.TryGetValue(policyName, out var policy))
        {
            options.Policies[policyName] = defaults;
            return;
        }

        Normalize(policy, defaults);
    }

    private static void Normalize(DistributedLockPolicyOptions policy, DistributedLockPolicyOptions defaults)
    {
        policy.AcquireTimeoutMs = policy.AcquireTimeoutMs <= 0 ? defaults.AcquireTimeoutMs : policy.AcquireTimeoutMs;
        policy.LeaseMs = policy.LeaseMs <= 0 ? defaults.LeaseMs : policy.LeaseMs;
        policy.RetryCount = policy.RetryCount < 0 ? defaults.RetryCount : policy.RetryCount;
        policy.RetryDelayMs = policy.RetryDelayMs <= 0 ? defaults.RetryDelayMs : policy.RetryDelayMs;
    }
}
