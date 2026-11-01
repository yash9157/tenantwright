using Shared.MultiTenancy;

namespace Shared.BackgroundTasks.Jobs;

public interface IBackgroundJobEnvelopeFactory
{
    BackgroundJobEnvelope Create<TArgs>(
        TArgs args,
        BackgroundJobEnqueueOptions? options = null);

    BackgroundJobEnvelope CreateRecurring<TArgs>(
        TArgs args,
        RecurringBackgroundJobOptions? options = null);
}

public sealed class BackgroundJobEnvelopeFactory : IBackgroundJobEnvelopeFactory
{
    private readonly IBackgroundJobRegistry _registry;
    private readonly IBackgroundJobSerializer _serializer;
    private readonly ICurrentTenant _currentTenant;

    public BackgroundJobEnvelopeFactory(
        IBackgroundJobRegistry registry,
        IBackgroundJobSerializer serializer,
        ICurrentTenant currentTenant)
    {
        _registry = registry;
        _serializer = serializer;
        _currentTenant = currentTenant;
    }

    public BackgroundJobEnvelope Create<TArgs>(
        TArgs args,
        BackgroundJobEnqueueOptions? options = null)
    {
        var job = _registry.GetRequired<TArgs>();
        var tenantId = ResolveTenantId(args, options?.TenantId);

        return new BackgroundJobEnvelope
        {
            JobName = job.Name,
            ArgsJson = _serializer.SerializeArgs(args),
            TenantId = tenantId,
            CorrelationId = NormalizeOptional(options?.CorrelationId),
            RequestedByUserId = options?.RequestedByUserId,
            EnqueuedAtUtc = DateTime.UtcNow,
            Priority = options?.Priority ?? BackgroundJobPriority.Normal,
            Queue = NormalizeOptional(options?.Queue),
            Headers = options?.Headers is { Count: > 0 }
                ? new Dictionary<string, string>(options.Headers, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    public BackgroundJobEnvelope CreateRecurring<TArgs>(
        TArgs args,
        RecurringBackgroundJobOptions? options = null)
    {
        var job = _registry.GetRequired<TArgs>();
        var tenantId = ResolveTenantId(args, options?.TenantId);

        return new BackgroundJobEnvelope
        {
            JobName = job.Name,
            ArgsJson = _serializer.SerializeArgs(args),
            TenantId = tenantId,
            CorrelationId = NormalizeOptional(options?.CorrelationId),
            RequestedByUserId = options?.RequestedByUserId,
            EnqueuedAtUtc = DateTime.UtcNow,
            Priority = BackgroundJobPriority.Normal,
            Queue = NormalizeOptional(options?.Queue),
            Headers = options?.Headers is { Count: > 0 }
                ? new Dictionary<string, string>(options.Headers, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private Guid? ResolveTenantId<TArgs>(TArgs args, Guid? explicitTenantId)
    {
        if (explicitTenantId.HasValue)
        {
            return explicitTenantId;
        }

        if (args is IBackgroundJobTenantArgs tenantArgs)
        {
            if (!tenantArgs.TenantId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Tenant-aware background job args '{typeof(TArgs).FullName}' must include TenantId.");
            }

            return tenantArgs.TenantId;
        }

        return _currentTenant.Id;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
