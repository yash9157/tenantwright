namespace Shared.BackgroundTasks.Jobs;

public sealed class BackgroundJobDefinition
{
    public string Name { get; }
    public Type JobType { get; }
    public Type ArgsType { get; }

    private BackgroundJobDefinition(string name, Type jobType, Type argsType)
    {
        Name = name;
        JobType = jobType;
        ArgsType = argsType;
    }

    public static BackgroundJobDefinition Create<TJob, TArgs>(string? name = null)
        where TJob : IBackgroundJob<TArgs>
    {
        return new BackgroundJobDefinition(
            NormalizeName(name ?? BackgroundJobNameAttribute.GetName(typeof(TArgs))),
            typeof(TJob),
            typeof(TArgs));
    }

    internal static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Background job name cannot be empty.");
        }

        return name.Trim();
    }
}
