using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>Durable outbox dispatch and retention, bound from <c>Messaging:Outbox</c>.</summary>
public sealed class TenantWrightOutboxOptions
{
    public const string SectionName = "Messaging:Outbox";

    /// <summary>Idle wait between claim attempts when no commit wake-up arrives. Default 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum rows one claim statement leases. Default 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long a claim excludes other dispatchers from its rows. Must cover at least two dispatch timeouts; a crashed
    /// dispatcher's rows become claimable again when it expires. Default 2 minutes.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound on one broker-confirmed dispatch. Default 30 seconds.</summary>
    public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts after which a still-failing row is parked for an operator. Default 12.</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Retry delay before the first retry; doubles per attempt with jitter. Default 1 second.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the exponential retry delay. Default 5 minutes.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long dispatched rows are kept for diagnosis before deletion. Default 7 days.</summary>
    public TimeSpan DispatchedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Cadence of the dispatched-row retention sweep. Default 5 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rows deleted per retention statement; a sweep repeats bounded statements. Default 500.</summary>
    public int CleanupBatchSize { get; set; } = 500;
}

/// <summary>Durable inbox retention, bound from <c>Messaging:Inbox</c>.</summary>
public sealed class TenantWrightInboxOptions
{
    public const string SectionName = "Messaging:Inbox";

    /// <summary>
    /// Retention beyond <see cref="MessagingTrustOptions.MaximumOriginAge"/>. Any delivery older than the origin age is
    /// rejected as stale, so keeping records at least that long, plus this margin, keeps deduplication complete for
    /// every delivery the verifier can still accept. Default 24 hours.
    /// </summary>
    public TimeSpan RetentionBeyondOriginAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Cadence of the inbox retention sweep. Default 15 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Records deleted per retention statement; a sweep repeats bounded statements. Default 1000.</summary>
    public int CleanupBatchSize { get; set; } = 1000;
}

internal sealed class TenantWrightOutboxOptionsValidator : IValidateOptions<TenantWrightOutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantWrightOutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.PollInterval < TimeSpan.FromMilliseconds(50) || options.PollInterval > TimeSpan.FromMinutes(1))
            failures.Add("Messaging:Outbox:PollInterval must be between 50 milliseconds and 1 minute.");
        if (options.BatchSize is < 1 or > 1000)
            failures.Add("Messaging:Outbox:BatchSize must be between 1 and 1000.");
        if (options.DispatchTimeout < TimeSpan.FromSeconds(1) || options.DispatchTimeout > TimeSpan.FromMinutes(5))
            failures.Add("Messaging:Outbox:DispatchTimeout must be between 1 second and 5 minutes.");
        if (options.LeaseDuration < options.DispatchTimeout * 2 || options.LeaseDuration > TimeSpan.FromHours(1))
            failures.Add("Messaging:Outbox:LeaseDuration must be at least twice DispatchTimeout and no more than 1 hour.");
        if (options.MaxAttempts is < 1 or > 100)
            failures.Add("Messaging:Outbox:MaxAttempts must be between 1 and 100.");
        if (options.RetryBaseDelay < TimeSpan.FromMilliseconds(10) || options.RetryBaseDelay > TimeSpan.FromMinutes(5))
            failures.Add("Messaging:Outbox:RetryBaseDelay must be between 10 milliseconds and 5 minutes.");
        if (options.RetryMaxDelay < options.RetryBaseDelay || options.RetryMaxDelay > TimeSpan.FromHours(6))
            failures.Add("Messaging:Outbox:RetryMaxDelay must be at least RetryBaseDelay and no more than 6 hours.");
        if (options.DispatchedRetention < TimeSpan.FromMinutes(1) || options.DispatchedRetention > TimeSpan.FromDays(90))
            failures.Add("Messaging:Outbox:DispatchedRetention must be between 1 minute and 90 days.");
        if (options.CleanupInterval < TimeSpan.FromSeconds(1) || options.CleanupInterval > TimeSpan.FromDays(1))
            failures.Add("Messaging:Outbox:CleanupInterval must be between 1 second and 1 day.");
        if (options.CleanupBatchSize is < 1 or > 10000)
            failures.Add("Messaging:Outbox:CleanupBatchSize must be between 1 and 10000.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class TenantWrightInboxOptionsValidator : IValidateOptions<TenantWrightInboxOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantWrightInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.RetentionBeyondOriginAge < TimeSpan.Zero || options.RetentionBeyondOriginAge > TimeSpan.FromDays(30))
            failures.Add("Messaging:Inbox:RetentionBeyondOriginAge must be between zero and 30 days.");
        if (options.CleanupInterval < TimeSpan.FromSeconds(1) || options.CleanupInterval > TimeSpan.FromDays(1))
            failures.Add("Messaging:Inbox:CleanupInterval must be between 1 second and 1 day.");
        if (options.CleanupBatchSize is < 1 or > 10000)
            failures.Add("Messaging:Inbox:CleanupBatchSize must be between 1 and 10000.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
