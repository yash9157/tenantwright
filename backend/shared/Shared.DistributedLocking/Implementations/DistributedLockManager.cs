using System.Diagnostics;
using Medallion.Threading.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using StackExchange.Redis;

namespace Shared.DistributedLocking.Implementations;

public sealed class DistributedLockManager : IDistributedLock
{
    private readonly IDistributedLockKeyNormalizer _keyNormalizer;
    private readonly IOptionsMonitor<DistributedLockingOptions> _optionsMonitor;
    private readonly IConnectionMultiplexer? _connectionMultiplexer;
    private readonly ILogger<DistributedLockManager> _logger;

    public DistributedLockManager(
        IDistributedLockKeyNormalizer keyNormalizer,
        IOptionsMonitor<DistributedLockingOptions> optionsMonitor,
        ILogger<DistributedLockManager> logger,
        IConnectionMultiplexer? connectionMultiplexer = null)
    {
        _keyNormalizer = keyNormalizer;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _connectionMultiplexer = connectionMultiplexer;
    }

    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string name,
        DistributedLockAcquireOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= DistributedLockAcquireOptions.Default;

        var resolved = ResolvePolicy(options);
        var normalizedKey = _keyNormalizer.NormalizeKey(
            new DistributedLockKeyNormalizeArgs(name, options.IgnoreTenant));

        var startedAt = Stopwatch.GetTimestamp();
        var attempts = Math.Max(1, resolved.RetryCount + 1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var handle = await TryAcquireCoreAsync(normalizedKey, resolved, cancellationToken);
                if (handle != null)
                {
                    if (resolved.EnableMetrics)
                    {
                        DistributedLockingTelemetry.RecordSuccess(
                            resolved.PolicyName,
                            resolved.FailureMode,
                            GetElapsedMilliseconds(startedAt));
                    }

                    return handle;
                }

                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(resolved.RetryDelayMs), cancellationToken);
                }
            }
            catch (Exception ex) when (ShouldSwallowException(resolved.FailureMode))
            {
                _logger.LogWarning(
                    ex,
                    "Distributed lock backend error swallowed in best-effort mode for key {LockKey} (policy {PolicyName}).",
                    normalizedKey,
                    resolved.PolicyName);

                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(resolved.RetryDelayMs), cancellationToken);
                    continue;
                }

                if (resolved.EnableMetrics)
                {
                    DistributedLockingTelemetry.RecordFailure(
                        resolved.PolicyName,
                        resolved.FailureMode,
                        "backend_error_swallowed",
                        GetElapsedMilliseconds(startedAt));
                }

                return null;
            }
            catch (Exception ex)
            {
                if (resolved.EnableMetrics)
                {
                    DistributedLockingTelemetry.RecordFailure(
                        resolved.PolicyName,
                        resolved.FailureMode,
                        "backend_error",
                        GetElapsedMilliseconds(startedAt));
                }

                throw new InvalidOperationException(
                    $"Distributed lock acquisition failed for key '{normalizedKey}' under policy '{resolved.PolicyName}'.",
                    ex);
            }
        }

        if (resolved.EnableMetrics)
        {
            DistributedLockingTelemetry.RecordContention(
                resolved.PolicyName,
                resolved.FailureMode,
                GetElapsedMilliseconds(startedAt));
        }

        return null;
    }

    public async Task<IDistributedLockHandle> AcquireAsync(
        string name,
        DistributedLockAcquireOptions options,
        CancellationToken cancellationToken = default)
    {
        var handle = await TryAcquireAsync(name, options, cancellationToken);
        if (handle != null)
        {
            return handle;
        }

        var resolved = ResolvePolicy(options ?? DistributedLockAcquireOptions.Default);
        throw new TimeoutException(
            $"Unable to acquire distributed lock '{name}' within {resolved.AcquireTimeoutMs}ms (policy: {resolved.PolicyName}).");
    }

    private async ValueTask<IDistributedLockHandle?> TryAcquireCoreAsync(
        string normalizedKey,
        ResolvedPolicy resolved,
        CancellationToken cancellationToken)
    {
        return resolved.Provider switch
        {
            DistributedLockProviderType.Valkey => await TryAcquireValkeyAsync(
                normalizedKey,
                resolved,
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Distributed lock provider '{resolved.Provider}' is not implemented.")
        };
    }

    private async ValueTask<IDistributedLockHandle?> TryAcquireValkeyAsync(
        string normalizedKey,
        ResolvedPolicy resolved,
        CancellationToken cancellationToken)
    {
        if (_connectionMultiplexer == null)
        {
            throw new InvalidOperationException(
                "IConnectionMultiplexer is not registered for Valkey distributed locking.");
        }

        var db = _connectionMultiplexer.GetDatabase(resolved.ValkeyDatabase);
        var lockExpiry = TimeSpan.FromMilliseconds(resolved.LeaseMs);
        var acquireTimeout = TimeSpan.FromMilliseconds(resolved.AcquireTimeoutMs);
        var minBusyWait = TimeSpan.FromMilliseconds(resolved.ValkeyBusyWaitMinMs);
        var maxBusyWait = TimeSpan.FromMilliseconds(Math.Max(resolved.ValkeyBusyWaitMinMs, resolved.ValkeyBusyWaitMaxMs));
        var extensionCadence = TimeSpan.FromMilliseconds(Math.Max(100, resolved.LeaseMs / 3));

        var valkeyLock = new RedisDistributedLock(normalizedKey, db, builder =>
        {
            builder.Expiry(lockExpiry);
            builder.ExtensionCadence(extensionCadence);
            builder.BusyWaitSleepTime(minBusyWait, maxBusyWait);
        });

        var handle = await valkeyLock.TryAcquireAsync(acquireTimeout, cancellationToken);
        return handle == null ? null : new MedallionDistributedLockHandle(normalizedKey, handle);
    }

    private ResolvedPolicy ResolvePolicy(DistributedLockAcquireOptions acquireOptions)
    {
        var moduleOptions = _optionsMonitor.CurrentValue;
        var provider = DistributedLockingEnvironmentGuard.ResolveProvider(moduleOptions.Provider);
        var defaultPolicy = Clone(moduleOptions.DefaultPolicy);
        ApplyDefaultPolicy(defaultPolicy);

        var policyName = acquireOptions.PolicyName ?? "Default";
        if (!string.Equals(policyName, "Default", StringComparison.OrdinalIgnoreCase) &&
            moduleOptions.Policies.TryGetValue(policyName, out var namedPolicy))
        {
            Merge(defaultPolicy, namedPolicy);
        }

        if (acquireOptions.AcquireTimeoutMs.HasValue)
        {
            defaultPolicy.AcquireTimeoutMs = acquireOptions.AcquireTimeoutMs.Value;
        }

        if (acquireOptions.LeaseMs.HasValue)
        {
            defaultPolicy.LeaseMs = acquireOptions.LeaseMs.Value;
        }

        if (acquireOptions.FailureMode.HasValue)
        {
            defaultPolicy.FailureMode = acquireOptions.FailureMode.Value;
        }

        if (acquireOptions.EnableMetrics.HasValue)
        {
            defaultPolicy.EnableMetrics = acquireOptions.EnableMetrics.Value;
        }

        if (acquireOptions.RetryCount.HasValue)
        {
            defaultPolicy.RetryCount = acquireOptions.RetryCount.Value;
        }

        if (acquireOptions.RetryDelayMs.HasValue)
        {
            defaultPolicy.RetryDelayMs = acquireOptions.RetryDelayMs.Value;
        }

        ApplyDefaultPolicy(defaultPolicy);

        return new ResolvedPolicy
        {
            PolicyName = policyName,
            Provider = provider,
            FailureMode = defaultPolicy.FailureMode,
            AcquireTimeoutMs = defaultPolicy.AcquireTimeoutMs,
            LeaseMs = defaultPolicy.LeaseMs,
            EnableMetrics = defaultPolicy.EnableMetrics,
            RetryCount = defaultPolicy.FailureMode == DistributedLockFailureMode.BestEffortWithRetry
                ? defaultPolicy.RetryCount
                : 0,
            RetryDelayMs = defaultPolicy.RetryDelayMs,
            ValkeyDatabase = moduleOptions.Valkey.Database,
            ValkeyBusyWaitMinMs = moduleOptions.Valkey.BusyWaitMinMs,
            ValkeyBusyWaitMaxMs = moduleOptions.Valkey.BusyWaitMaxMs
        };
    }

    private static bool ShouldSwallowException(DistributedLockFailureMode mode)
    {
        return mode == DistributedLockFailureMode.BestEffort ||
               mode == DistributedLockFailureMode.BestEffortWithRetry ||
               mode == DistributedLockFailureMode.PerUse;
    }

    private static double GetElapsedMilliseconds(long startedTimestamp)
    {
        return Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
    }

    private static void Merge(DistributedLockPolicyOptions target, DistributedLockPolicyOptions source)
    {
        target.AcquireTimeoutMs = source.AcquireTimeoutMs;
        target.LeaseMs = source.LeaseMs;
        target.FailureMode = source.FailureMode;
        target.EnableMetrics = source.EnableMetrics;
        target.RetryCount = source.RetryCount;
        target.RetryDelayMs = source.RetryDelayMs;
    }

    private static DistributedLockPolicyOptions Clone(DistributedLockPolicyOptions source)
    {
        return new DistributedLockPolicyOptions
        {
            AcquireTimeoutMs = source.AcquireTimeoutMs,
            LeaseMs = source.LeaseMs,
            FailureMode = source.FailureMode,
            EnableMetrics = source.EnableMetrics,
            RetryCount = source.RetryCount,
            RetryDelayMs = source.RetryDelayMs
        };
    }

    private static void ApplyDefaultPolicy(DistributedLockPolicyOptions policy)
    {
        policy.AcquireTimeoutMs = Math.Max(0, policy.AcquireTimeoutMs);
        policy.LeaseMs = Math.Max(250, policy.LeaseMs);
        policy.RetryCount = Math.Max(0, policy.RetryCount);
        policy.RetryDelayMs = Math.Max(10, policy.RetryDelayMs);
    }

    private sealed class ResolvedPolicy
    {
        public required string PolicyName { get; init; }
        public required DistributedLockProviderType Provider { get; init; }
        public required DistributedLockFailureMode FailureMode { get; init; }
        public required int AcquireTimeoutMs { get; init; }
        public required int LeaseMs { get; init; }
        public required bool EnableMetrics { get; init; }
        public required int RetryCount { get; init; }
        public required int RetryDelayMs { get; init; }
        public required int ValkeyDatabase { get; init; }
        public required int ValkeyBusyWaitMinMs { get; init; }
        public required int ValkeyBusyWaitMaxMs { get; init; }
    }
}
