using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using System.Diagnostics;

namespace Shared.MultiTenancy;

public class TenantResolver : ITenantResolver, ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<SharedTenantResolveOptions> _options;
    private readonly ILogger<TenantResolver> _logger;

    public TenantResolver(
        IServiceProvider serviceProvider,
        IOptionsMonitor<SharedTenantResolveOptions> options,
        ILogger<TenantResolver> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    public virtual async Task<TenantResolveResult> ResolveAsync(
        TenantResolveRequestContext context,
        CancellationToken cancellationToken = default)
    {
        var timer = TenantResolutionMetrics.StartTimer();
        var options = _options.CurrentValue;

        var result = new TenantResolveResult
        {
            Status = TenantResolveStatus.Unresolved
        };

        TenantResolveContributorResult? currentUser = null;
        TenantResolveContributorResult? header = null;
        foreach (var resolverType in options.Resolvers)
        {
            if (!typeof(ITenantResolveContributor).IsAssignableFrom(resolverType))
            {
                continue;
            }

            var contributor = (ITenantResolveContributor)_serviceProvider.GetRequiredService(resolverType);
            result.AddAppliedResolver(contributor.Name);

            var contributorResult = await contributor.ResolveAsync(context, options, cancellationToken);

            if (contributor is CurrentUserTenantResolveContributor)
            {
                currentUser = contributorResult;
            }
            else if (contributor is HeaderTenantResolveContributor)
            {
                header = contributorResult;
            }

            if (contributorResult.Status == TenantResolveStatus.AmbiguousInput && options.FailOnAmbiguousTenantInput)
            {
                result.Status = TenantResolveStatus.AmbiguousInput;
                result.Source = contributor.Name;
                result.FailureReason = contributorResult.FailureReason;
                return Complete(timer, result);
            }

            if (contributorResult.Status == TenantResolveStatus.InvalidInput && options.FailOnInvalidTenantInput)
            {
                result.Status = TenantResolveStatus.InvalidInput;
                result.Source = contributor.Name;
                result.FailureReason = contributorResult.FailureReason;
                return Complete(timer, result);
            }
        }

        if (currentUser?.Status == TenantResolveStatus.Resolved)
        {
            if (header?.Status == TenantResolveStatus.Resolved && header.TenantId != currentUser.TenantId)
            {
                result.Status = TenantResolveStatus.TenantMismatch;
                result.Source = CurrentUserTenantResolveContributor.ContributorName;
                result.TenantId = currentUser.TenantId;
                result.FailureReason = "Request tenant input does not match authenticated user tenant.";
                return Complete(timer, result);
            }

            result.Status = TenantResolveStatus.Resolved;
            result.Source = CurrentUserTenantResolveContributor.ContributorName;
            result.TenantId = currentUser.TenantId;
            return Complete(timer, result);
        }

        if (header?.Status == TenantResolveStatus.Resolved)
        {
            result.Status = TenantResolveStatus.Resolved;
            result.Source = HeaderTenantResolveContributor.ContributorName;
            result.TenantId = header.TenantId;
            return Complete(timer, result);
        }

        result.Status = TenantResolveStatus.Unresolved;
        result.Source = "none";
        return Complete(timer, result);
    }

    private TenantResolveResult Complete(Stopwatch timer, TenantResolveResult result)
    {
        TenantResolutionMetrics.RecordResolution(result);
        TenantResolutionMetrics.RecordResolutionDuration(timer, result);

        if (result.Status is TenantResolveStatus.InvalidInput or TenantResolveStatus.AmbiguousInput or TenantResolveStatus.TenantMismatch)
        {
            _logger.LogWarning(
                "Tenant resolution rejected request. Status={Status}, Source={Source}, TenantHash={TenantHash}, Reason={Reason}",
                result.Status,
                result.Source ?? "none",
                TenantResolutionHash.HashTenantId(result.TenantId),
                result.FailureReason ?? "<none>");
        }

        return result;
    }
}
