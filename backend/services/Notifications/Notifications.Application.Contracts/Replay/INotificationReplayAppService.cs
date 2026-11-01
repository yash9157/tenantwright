namespace Notifications.Application.Contracts.Replay;

public interface INotificationReplayAppService
{
    Task<ReplayNotificationResultDto> ReplayAsync(ReplayNotificationInput input, CancellationToken cancellationToken = default);

    Task<ReplayNotificationOverridePreviewDto> PreviewOverrideAsync(
        PreviewReplayNotificationOverrideInput input,
        CancellationToken cancellationToken = default);

    Task<ReplayNotificationResultDto> ReplayOverrideAsync(
        ReplayNotificationOverrideInput input,
        CancellationToken cancellationToken = default);
}
