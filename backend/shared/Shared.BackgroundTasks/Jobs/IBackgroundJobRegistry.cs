using Microsoft.Extensions.Options;

namespace Shared.BackgroundTasks.Jobs;

public interface IBackgroundJobRegistry
{
    BackgroundJobDefinition GetRequired(string jobName);
    BackgroundJobDefinition GetRequired<TArgs>();
}

public sealed class BackgroundJobRegistry : IBackgroundJobRegistry
{
    private readonly Dictionary<string, BackgroundJobDefinition> _jobsByName;
    private readonly Dictionary<Type, BackgroundJobDefinition> _jobsByArgsType;

    public BackgroundJobRegistry(IOptions<BackgroundTasksOptions> options)
    {
        _jobsByName = new Dictionary<string, BackgroundJobDefinition>(StringComparer.Ordinal);
        _jobsByArgsType = new Dictionary<Type, BackgroundJobDefinition>();

        foreach (var job in options.Value.Jobs)
        {
            if (!_jobsByName.TryAdd(job.Name, job))
            {
                throw new InvalidOperationException($"Duplicate background job name '{job.Name}'.");
            }

            if (!_jobsByArgsType.TryAdd(job.ArgsType, job))
            {
                throw new InvalidOperationException(
                    $"Duplicate background job args type '{job.ArgsType.FullName}'.");
            }
        }
    }

    public BackgroundJobDefinition GetRequired(string jobName)
    {
        var normalizedName = BackgroundJobDefinition.NormalizeName(jobName);
        if (_jobsByName.TryGetValue(normalizedName, out var job))
        {
            return job;
        }

        throw new InvalidOperationException($"Background job '{normalizedName}' is not registered.");
    }

    public BackgroundJobDefinition GetRequired<TArgs>()
    {
        if (_jobsByArgsType.TryGetValue(typeof(TArgs), out var job))
        {
            return job;
        }

        throw new InvalidOperationException(
            $"Background job args type '{typeof(TArgs).FullName}' is not registered.");
    }
}
