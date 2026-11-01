using Shared.BackgroundTasks.Jobs;

namespace Shared.BackgroundTasks.Hangfire;

internal static class HangfireQueueSelector
{
    public static string SelectQueue(BackgroundJobEnvelope envelope, bool recurring = false)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Queue))
        {
            return BackgroundTasksHangfireOptionsPostConfigure.NormalizeQueue(envelope.Queue);
        }

        if (recurring)
        {
            return "recurring";
        }

        return envelope.Priority switch
        {
            BackgroundJobPriority.Critical => "critical",
            BackgroundJobPriority.High => "critical",
            BackgroundJobPriority.Low => "low",
            _ => "default"
        };
    }
}
