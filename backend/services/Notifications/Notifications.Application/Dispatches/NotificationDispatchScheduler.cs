using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Application.Delivery;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;

namespace Notifications.Application.Dispatches;

/// <summary>Admits durable intent in the caller's inbox/request transaction; it performs no external I/O.</summary>
public sealed class NotificationDispatchScheduler(
    IUnitOfWorkManager unitOfWorkManager,
    IDbContextProvider<NotificationsDbContextApp> contextProvider,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider,
    IOptions<NotificationDispatchOptions> options,
    IOptions<NotificationsDeliveryOptions> deliveryOptions,
    IBackgroundWorkerWakeSignal wakeSignal,
    ILogger<NotificationDispatchScheduler> logger) : INotificationDispatchScheduler, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<NotificationDispatchAdmissionDto> ScheduleAsync(NotificationSendRequestedTenantEto input, CancellationToken cancellationToken = default)
    {
        if (input.TenantId is null || input.TenantId == Guid.Empty)
            throw InvalidPlan();
        return ScheduleCoreAsync(input, input.TenantId, input.RequestId, input.DeliveryPlan, input.Recipients,
            new Metadata(input.SourceService, input.Category, input.Priority, input.ActionUrl, input.OccurredAtUtc,
                input.ScheduledForUtc, input.ExpiresAtUtc, input.RespectUserPreferences, input.PreferenceKey), cancellationToken);
    }

    public Task<NotificationDispatchAdmissionDto> ScheduleAsync(NotificationSendRequestedHostEto input, CancellationToken cancellationToken = default) =>
        ScheduleCoreAsync(input, null, input.RequestId, input.DeliveryPlan, input.Recipients,
            new Metadata(input.SourceService, input.Category, input.Priority, input.ActionUrl, input.OccurredAtUtc,
                input.ScheduledForUtc, input.ExpiresAtUtc, input.RespectUserPreferences, input.PreferenceKey), cancellationToken);

    private async Task<NotificationDispatchAdmissionDto> ScheduleCoreAsync(object request, Guid? tenantId, Guid requestId,
        NotificationDeliveryPlan plan, IReadOnlyList<NotificationRecipientPlan> recipients,
        Metadata metadata, CancellationToken cancellationToken)
    {
        if (currentTenant.Id != tenantId)
            throw new SharedForbiddenException(NotificationErrorCodes.DispatchScopeMismatch, "Notification intent must match the current scope.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        Validate(requestId, plan, recipients, metadata, now);
        var canonical = TenantWrightContractSerializer.SerializeCanonical(request, request.GetType());
        if (canonical.Length > options.Value.MaximumPayloadBytes)
            throw InvalidPlan();
        (plan, recipients, metadata) = Freeze(canonical, request.GetType(), tenantId, requestId);
        Validate(requestId, plan, recipients, metadata, now);
        var fingerprint = TenantWrightAttestationService.Sha256(canonical);
        var scopeId = tenantId ?? Guid.Empty;

        using var unitOfWork = unitOfWorkManager.Begin(new UnitOfWorkOptions { IsTransactional = true });
        var context = await contextProvider.GetDbContextAsync();
        var existing = await context.NotificationDispatches
            .SingleOrDefaultAsync(dispatch => dispatch.ScopeId == scopeId && dispatch.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint)
                throw new SharedConflictException(NotificationErrorCodes.DispatchRequestConflict, "This request identity already represents different notification content.");
            unitOfWork.OnCompleted(() =>
            {
                NotificationDispatchTelemetry.Admission(existing.Mode, duplicate: true);
                return Task.CompletedTask;
            });
            await unitOfWork.CompleteAsync(cancellationToken);
            return new NotificationDispatchAdmissionDto(existing.Id, requestId, true);
        }

        var activation = metadata.ScheduledForUtc ?? now;
        var expires = metadata.ExpiresAtUtc ?? activation.AddDays(options.Value.DefaultLifetimeDays);
        var mode = plan.Mode switch
        {
            NotificationDeliveryMode.Parallel => NotificationDispatchMode.Parallel,
            NotificationDeliveryMode.OrderedFallback => NotificationDispatchMode.OrderedFallback,
            _ => throw InvalidPlan()
        };
        var dispatch = new NotificationDispatch(Guid.CreateVersion7(), tenantId, requestId, fingerprint,
            metadata.SourceService, mode, activation, expires, now);
        foreach (var recipient in recipients)
        {
            var route = new NotificationDispatchRecipient(Guid.CreateVersion7(), tenantId, dispatch.Id, recipient.Key, recipient.PreferenceUserId);
            for (var ordinal = 0; ordinal < plan.Steps.Count; ordinal++)
            {
                var channelPlan = plan.Steps[ordinal];
                var channel = (NotificationChannel)(int)channelPlan.Channel;
                var stepId = Guid.CreateVersion7();
                var destinations = recipient.Destinations.Where(destination => destination.Channel == channelPlan.Channel)
                    .Select(destination => new NotificationRecipientEto
                    {
                        RecipientType = destination.RecipientType,
                        RecipientValue = destination.RecipientValue
                    }).ToList();
                var channelInput = new NotificationChannelRequestHost
                {
                    RequestId = stepId,
                    DispatchStepId = stepId,
                    Channel = channelPlan.Channel,
                    Subject = channelPlan.Subject,
                    TemplateName = channelPlan.TemplateName,
                    TemplateModel = channelPlan.TemplateModel,
                    Recipients = destinations,
                    Category = metadata.Category,
                    Priority = metadata.Priority,
                    ActionUrl = metadata.ActionUrl,
                    OccurredAtUtc = metadata.OccurredAtUtc,
                    ScheduledForUtc = metadata.ScheduledForUtc,
                    SourceService = metadata.SourceService,
                    RespectUserPreferences = plan.Steps.Count > 1 || metadata.RespectUserPreferences,
                    PreferenceUserId = recipient.PreferenceUserId,
                    PreferenceKey = metadata.PreferenceKey
                };
                var step = new NotificationDispatchStep(stepId, tenantId, dispatch.Id, route.Id, channel, ordinal,
                    JsonSerializer.Serialize(channelInput, JsonOptions), activation);
                if (destinations.Count == 0)
                    step.Observe(NotificationDispatchState.Suppressed, "destination_not_selected");
                route.AddStep(step);
            }
            dispatch.AddRecipient(route);
        }
        dispatch.RefreshSummary();
        context.NotificationDispatches.Add(dispatch);
        await context.SaveChangesAsync(cancellationToken);
        unitOfWork.OnCompleted(() =>
        {
            NotificationDispatchTelemetry.Admission(mode, duplicate: false);
            return SignalAfterCommitAsync();
        });
        await unitOfWork.CompleteAsync(cancellationToken);
        return new NotificationDispatchAdmissionDto(dispatch.Id, requestId, false);
    }

    private void Validate(Guid requestId, NotificationDeliveryPlan plan,
        IReadOnlyList<NotificationRecipientPlan> recipients, Metadata metadata, DateTime now)
    {
        if (requestId == Guid.Empty || plan is null || !Enum.IsDefined(plan.Mode) || plan.Steps is null ||
            plan.Steps.Count is < 1 or > 5 || recipients is null || recipients.Count == 0 ||
            recipients.Count > options.Value.MaximumRecipients || recipients.Any(recipient => recipient is null) ||
            string.IsNullOrWhiteSpace(metadata.SourceService) || metadata.SourceService.Length > 128 ||
            metadata.OccurredAtUtc.Kind != DateTimeKind.Utc || !Enum.IsDefined(metadata.Priority) ||
            metadata.PreferenceKey?.Length > 128 || metadata.Category?.Length > 128 || metadata.ActionUrl?.Length > 2048)
            throw InvalidPlan();
        if (metadata.ActionUrl is { Length: > 0 } actionUrl &&
            (!actionUrl.StartsWith('/') || actionUrl.StartsWith("//", StringComparison.Ordinal) || actionUrl.Contains('\\', StringComparison.Ordinal)))
            throw InvalidPlan();
        var activation = metadata.ScheduledForUtc ?? now;
        var expiry = metadata.ExpiresAtUtc ?? activation.AddDays(options.Value.DefaultLifetimeDays);
        if (activation.Kind != DateTimeKind.Utc || activation > now.AddDays(deliveryOptions.Value.MaxScheduleHorizonDays) ||
            expiry.Kind != DateTimeKind.Utc || expiry <= activation || expiry - activation > TimeSpan.FromDays(30))
            throw InvalidPlan();
        if (plan.Steps.Any(step => step is null || !Enum.IsDefined(step.Channel) ||
                string.IsNullOrWhiteSpace(step.TemplateName) || step.TemplateName.Length > 256 || step.Subject?.Length > 256 ||
                step.TemplateModel is null || step.TemplateModel.Count > 128 ||
                step.TemplateModel.Any(pair => pair.Key.Length > 256 || pair.Value?.Length > 8192)) ||
            plan.Steps.Select(step => step.Channel).Distinct().Count() != plan.Steps.Count)
            throw InvalidPlan();
        var channels = plan.Steps.Select(step => step.Channel).ToHashSet();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.Key) || recipient.Key.Length > 128 || !keys.Add(recipient.Key) ||
                recipient.PreferenceUserId == Guid.Empty || recipient.Destinations is null || recipient.Destinations.Count == 0 ||
                recipient.Destinations.Count > 50)
                throw InvalidPlan();
            total += recipient.Destinations.Count;
            if (total > options.Value.MaximumTotalDestinations)
                throw InvalidPlan();
            foreach (var group in recipient.Destinations.GroupBy(destination => destination?.Channel))
            {
                if (!group.Key.HasValue || !channels.Contains(group.Key.Value) || group.Count() > options.Value.MaximumDestinationsPerChannel)
                    throw InvalidPlan();
                foreach (var destination in group)
                {
                    if (destination is null || string.IsNullOrWhiteSpace(destination.RecipientValue) || destination.RecipientValue.Length > 4096)
                        throw InvalidPlan();
                    var expectedType = destination.Channel switch
                    {
                        NotificationSendChannel.Email => NotificationSendRecipientType.EmailAddress,
                        NotificationSendChannel.Sms or NotificationSendChannel.WhatsApp => NotificationSendRecipientType.PhoneNumber,
                        NotificationSendChannel.App => NotificationSendRecipientType.UserId,
                        NotificationSendChannel.Push => NotificationSendRecipientType.DeviceToken,
                        _ => throw InvalidPlan()
                    };
                    var singleChannelGroup = plan.Steps.Count == 1 && destination.Channel == NotificationSendChannel.App &&
                        destination.RecipientType is NotificationSendRecipientType.Role or NotificationSendRecipientType.AllTenantUsers;
                    if (destination.RecipientType != expectedType && !singleChannelGroup)
                        throw InvalidPlan();
                    if (destination.RecipientType == NotificationSendRecipientType.UserId &&
                        (!Guid.TryParse(destination.RecipientValue, out var userId) || userId == Guid.Empty ||
                         recipient.PreferenceUserId.HasValue && recipient.PreferenceUserId.Value != userId))
                        throw InvalidPlan();
                }
            }
        }
    }

    private async Task SignalAfterCommitAsync()
    {
        try
        {
            await wakeSignal.SignalAsync(NotificationDeliveryWakeKeys.Delivery,
                NotificationDeliveryWakeKeys.CreateWakeOptions(deliveryOptions.Value), CancellationToken.None);
        }
        catch (Exception)
        {
            logger.LogWarning("Notification dispatch wake signal failed; durable polling will resume it.");
        }
    }

    private static SharedValidationException InvalidPlan() =>
        new(NotificationErrorCodes.DispatchPlanInvalid, "The notification dispatch plan, recipients or scheduling bounds are invalid.");

    private static (NotificationDeliveryPlan Plan, IReadOnlyList<NotificationRecipientPlan> Recipients, Metadata Metadata) Freeze(
        byte[] canonical, Type contractType, Guid? tenantId, Guid requestId)
    {
        // The fingerprint and persisted steps must describe the same immutable input even if a caller retains its DTO.
        var snapshot = JsonSerializer.Deserialize(canonical, contractType, JsonOptions);
        return snapshot switch
        {
            NotificationSendRequestedTenantEto tenant when tenant.TenantId == tenantId && tenant.RequestId == requestId =>
                (tenant.DeliveryPlan, tenant.Recipients, new Metadata(tenant.SourceService, tenant.Category, tenant.Priority,
                    tenant.ActionUrl, tenant.OccurredAtUtc, tenant.ScheduledForUtc, tenant.ExpiresAtUtc, tenant.RespectUserPreferences, tenant.PreferenceKey)),
            NotificationSendRequestedHostEto host when tenantId is null && host.RequestId == requestId =>
                (host.DeliveryPlan, host.Recipients, new Metadata(host.SourceService, host.Category, host.Priority,
                    host.ActionUrl, host.OccurredAtUtc, host.ScheduledForUtc, host.ExpiresAtUtc, host.RespectUserPreferences, host.PreferenceKey)),
            _ => throw InvalidPlan()
        };
    }

    private sealed record Metadata(string SourceService, string? Category, NotificationSendPriority Priority,
        string? ActionUrl, DateTime OccurredAtUtc, DateTime? ScheduledForUtc, DateTime? ExpiresAtUtc,
        bool RespectUserPreferences, string? PreferenceKey);
}
