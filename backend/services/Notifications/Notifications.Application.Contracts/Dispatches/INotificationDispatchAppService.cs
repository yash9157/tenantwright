using Notifications.Application.Contracts.Common;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Dispatches;

public interface INotificationDispatchAppService
{
    Task<PagedResultDto<NotificationDispatchDto>> GetListAsync(GetNotificationDispatchesInput input, bool requireTenant,
        CancellationToken cancellationToken = default);
    Task<NotificationDispatchDto> GetAsync(Guid id, bool requireTenant, CancellationToken cancellationToken = default);
    Task<PagedResultDto<NotificationDispatchRecipientDto>> GetRecipientsAsync(Guid id, NotificationDispatchPageInput input,
        bool requireTenant, CancellationToken cancellationToken = default);
    Task<PagedResultDto<NotificationDispatchEventDto>> GetHistoryAsync(Guid id, NotificationDispatchPageInput input,
        bool requireTenant, CancellationToken cancellationToken = default);
    Task<NotificationDispatchDto> CancelAsync(Guid id, bool requireTenant, CancellationToken cancellationToken = default);
    Task<NotificationDispatchDto> RetryStepAsync(Guid id, Guid stepId, RetryNotificationDispatchStepInput input,
        bool requireTenant, CancellationToken cancellationToken = default);
}

public sealed class RetryNotificationDispatchStepInput
{
    public long ExpectedRevision { get; set; }
}

public class NotificationDispatchPageInput
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public sealed class GetNotificationDispatchesInput : NotificationDispatchPageInput
{
    public Guid? TenantId { get; set; }
    public NotificationDispatchState? State { get; set; }
    public NotificationDispatchMode? Mode { get; set; }
    public Guid? RequestId { get; set; }
}

public sealed record NotificationDispatchDto(Guid Id, Guid RequestId, Guid? TenantId, string SourceService,
    NotificationDispatchMode Mode, NotificationDispatchState State, DateTime ActivationTimeUtc, DateTime ExpiresAtUtc,
    DateTime? CancelRequestedAtUtc, long Revision, int RecipientCount);

public sealed record NotificationDispatchRecipientDto(Guid Id, string? RecipientKey, Guid? PreferenceUserId,
    NotificationDispatchState State, bool SensitiveFieldsWithheld, IReadOnlyList<NotificationDispatchStepDto> Steps);

public sealed record NotificationDispatchStepDto(Guid Id, NotificationChannel Channel, int Ordinal,
    NotificationDispatchState State, string? OutcomeReason, Guid? NotificationId, string? ContentJson);

public sealed record NotificationDispatchEventDto(Guid Id, long Revision, NotificationDispatchEventKind Kind,
    DateTime OccurredAtUtc, Guid? RecipientId, Guid? StepId, string? Reason, string? ActorId);
