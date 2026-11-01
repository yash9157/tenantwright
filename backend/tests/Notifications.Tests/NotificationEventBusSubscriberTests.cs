using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Events;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Xunit;

namespace Notifications.Tests;

public class NotificationEventBusHandlerTests
{
    [Fact]
    public async Task HandleEventAsync_schedules_received_event()
    {
        var currentTenant = new TestCurrentTenant(null);
        var scheduler = new CapturingScheduler(currentTenant);
        var handler = new NotificationSendRequestedEventHandler(
            scheduler,
            currentTenant,
            NullLogger<NotificationSendRequestedEventHandler>.Instance);

        var eto = CreateEmailEvent();

        await handler.HandleAsync(eto);

        Assert.Equal(1, scheduler.CallCount);
        Assert.NotNull(scheduler.LastInput);
        Assert.Equal(eto.RequestId, scheduler.LastInput!.RequestId);
        Assert.Equal(eto.TenantId, scheduler.LastCurrentTenantId);
        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public async Task HandleEventAsync_should_swallow_disabled_channel_exception()
    {
        var scheduler = new ThrowingScheduler(
            new NotificationChannelDisabledByFeatureException("Email", "Notifications.Email"));
        var currentTenant = new TestCurrentTenant(null);
        var handler = new NotificationSendRequestedEventHandler(
            scheduler,
            currentTenant,
            NullLogger<NotificationSendRequestedEventHandler>.Instance);

        await handler.HandleAsync(CreateEmailEvent());

        Assert.Equal(1, scheduler.CallCount);
    }

    [Fact]
    public async Task HandleEventAsync_should_rethrow_unexpected_exception()
    {
        var scheduler = new ThrowingScheduler(new InvalidOperationException("unexpected"));
        var currentTenant = new TestCurrentTenant(null);
        var handler = new NotificationSendRequestedEventHandler(
            scheduler,
            currentTenant,
            NullLogger<NotificationSendRequestedEventHandler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(CreateEmailEvent()));
        Assert.Equal(1, scheduler.CallCount);
    }

    private static NotificationSendRequestedTenantEto CreateEmailEvent()
    {
        return new NotificationSendRequestedTenantEto
        {
            RequestId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            Channel = NotificationSendChannel.Email,
            TemplateName = "Tenant.TenantCreated",
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = "u1@test.local"
                }
            ]
        };
    }

    private sealed class CapturingScheduler : INotificationDeliveryScheduler
    {
        private readonly ICurrentTenant _currentTenant;

        public CapturingScheduler(ICurrentTenant currentTenant)
        {
            _currentTenant = currentTenant;
        }

        public int CallCount { get; private set; }
        public CapturedNotificationRequest? LastInput { get; private set; }
        public Guid? LastCurrentTenantId { get; private set; }

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationSendRequestedTenantEto input,
            CancellationToken cancellationToken = default)
            => CaptureAsync(CapturedNotificationRequest.From(input));

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationSendRequestedHostEto input,
            CancellationToken cancellationToken = default)
            => CaptureAsync(CapturedNotificationRequest.From(input));

        private Task<NotificationScheduleResult> CaptureAsync(CapturedNotificationRequest input)
        {
            CallCount++;
            LastInput = input;
            LastCurrentTenantId = _currentTenant.Id;
            return Task.FromResult(new NotificationScheduleResult
            {
                Accepted = true,
                RequestId = input.RequestId,
                NotificationId = Guid.CreateVersion7(),
                QueuedCount = input.Recipients.Count
            });
        }
    }

    private sealed class ThrowingScheduler : INotificationDeliveryScheduler
    {
        private readonly Exception _exception;

        public ThrowingScheduler(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationSendRequestedTenantEto input,
            CancellationToken cancellationToken = default)
            => Throw();

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationSendRequestedHostEto input,
            CancellationToken cancellationToken = default)
            => Throw();

        private Task<NotificationScheduleResult> Throw()
        {
            CallCount++;
            throw _exception;
        }
    }
}
