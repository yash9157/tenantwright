namespace Shared.BackgroundTasks.Jobs;

public sealed class BackgroundTasksOptions
{
    public const string SectionName = "BackgroundTasks";

    public int MaxPayloadBytes { get; set; } = 32 * 1024;
    public List<BackgroundJobDefinition> Jobs { get; } = [];
}

internal static class BackgroundTasksOptionsPostConfigure
{
    public static void Normalize(BackgroundTasksOptions options)
    {
        options.MaxPayloadBytes = Math.Clamp(options.MaxPayloadBytes, 1024, 1024 * 1024);
    }
}
