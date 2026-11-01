namespace Notifications.Application.Contracts.Notifications;

public interface INotificationDeliveryOperationsAppService
{
    Task<ResendNotificationDeliveryResultDto> ResendDeliveryAsync(
        ResendNotificationDeliveryInput input,
        CancellationToken cancellationToken = default);

    Task<ResendNotificationDeliveryResultDto> ResendFailedAsync(
        ResendFailedNotificationDeliveriesInput input,
        CancellationToken cancellationToken = default);

    Task<NotificationDeliveryOverridePreviewDto> PreviewOverrideResendAsync(
        PreviewNotificationDeliveryOverrideResendInput input,
        CancellationToken cancellationToken = default);

    Task<ResendNotificationDeliveryResultDto> ResendOverrideAsync(
        ResendNotificationDeliveryOverrideInput input,
        CancellationToken cancellationToken = default);

    Task<ReconcileNotificationDeliveryResultDto> ConfirmDeliveryAsync(
        ConfirmNotificationDeliveryInput input,
        CancellationToken cancellationToken = default);

    Task<ReconcileNotificationDeliveryResultDto> RetryDeliveryAsync(
        RetryNotificationDeliveryInput input,
        CancellationToken cancellationToken = default);
}
