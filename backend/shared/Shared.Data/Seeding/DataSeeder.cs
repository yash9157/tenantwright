using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;
using Shared.Uow.Options;

namespace Shared.Data.Seeding;

public class DataSeeder : IDataSeeder, ITransientDependency
{
    private static readonly Meter Meter = new("Shared.Data.Seeding");
    private static readonly Counter<long> ContributorExecutionCounter =
        Meter.CreateCounter<long>("shared.data.seeding.contributor_executions_total");
    private static readonly Counter<long> ContributorFailureCounter =
        Meter.CreateCounter<long>("shared.data.seeding.contributor_failures_total");

    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected SharedDataSeedOptions Options { get; }

    public DataSeeder(
        IOptions<SharedDataSeedOptions> options,
        IUnitOfWorkManager unitOfWorkManager)
    {
        UnitOfWorkManager = unitOfWorkManager;
        Options = options.Value;
    }

    [UnitOfWork]
    public virtual async Task SeedAsync(DataSeedContext context)
    {
        if (context.Properties.ContainsKey(DataSeederExtensions.SeedInSeparateUow))
        {
            foreach (var contributorType in Options.Contributors)
            {
                var options = context.Properties.TryGetValue(DataSeederExtensions.SeedInSeparateUowOptions, out var uowOptions)
                    ? (UnitOfWorkOptions)uowOptions!
                    : new UnitOfWorkOptions();
                var requiresNew = context.Properties.TryGetValue(
                    DataSeederExtensions.SeedInSeparateUowRequiresNew,
                    out var requiresNewValue) &&
                    (bool)requiresNewValue!;

                await using var uow = UnitOfWorkManager.Begin(options, requiresNew);
                var contributor = (IDataSeedContributor)uow.ServiceProvider.GetRequiredService(contributorType);
                await ExecuteContributorAsync(contributor, context);
                await uow.CompleteAsync();
            }

            return;
        }

        var currentUnitOfWork = UnitOfWorkManager.Current
            ?? throw new InvalidOperationException("Data contributors can only be resolved inside an active unit of work.");
        foreach (var contributorType in Options.Contributors)
        {
            var contributor = (IDataSeedContributor)currentUnitOfWork.ServiceProvider
                .GetRequiredService(contributorType);
            await ExecuteContributorAsync(contributor, context);
        }
    }

    private static async Task ExecuteContributorAsync(IDataSeedContributor contributor, DataSeedContext context)
    {
        var contributorName = contributor.GetType().FullName ?? contributor.GetType().Name;

        try
        {
            await contributor.SeedAsync(context);
            ContributorExecutionCounter.Add(1, new KeyValuePair<string, object?>("contributor", contributorName));
        }
        catch
        {
            ContributorFailureCounter.Add(1, new KeyValuePair<string, object?>("contributor", contributorName));
            throw;
        }
    }
}
