using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Domain.Shared.Constants;
using Notifications.Events;
using Shared;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationEventBusHandlerTests
{
    [Fact]
    public async Task Tenant_command_admits_one_plan_in_the_restored_scope()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(tenantId);
        var scheduler = new CapturingScheduler();
        var handler = new NotificationSendRequestedEventHandler(scheduler, currentTenant, NullLogger<NotificationSendRequestedEventHandler>.Instance);
        var message = TenantRequest(tenantId);
        await handler.HandleAsync(message);
        Assert.Same(message, Assert.Single(scheduler.Requests));
        Assert.Equal(tenantId, currentTenant.Id);
    }

    [Fact]
    public async Task Tenant_body_cannot_select_a_tenant_from_host_or_another_tenant_scope()
    {
        foreach (var scope in new Guid?[] { null, Guid.NewGuid() })
        {
            var scheduler = new CapturingScheduler();
            var handler = new NotificationSendRequestedEventHandler(scheduler, new TestCurrentTenant(scope), NullLogger<NotificationSendRequestedEventHandler>.Instance);
            var failure = await Assert.ThrowsAsync<SharedForbiddenException>(() => handler.HandleAsync(TenantRequest(Guid.NewGuid())));
            Assert.Equal(NotificationErrorCodes.DispatchScopeMismatch, failure.Code);
            Assert.Empty(scheduler.Requests);
        }
    }

    [Fact]
    public async Task Host_command_is_rejected_inside_a_tenant_scope()
    {
        var scheduler = new CapturingScheduler();
        var handler = new NotificationSendRequestedEventHandler(scheduler, new TestCurrentTenant(Guid.NewGuid()), NullLogger<NotificationSendRequestedEventHandler>.Instance);
        await Assert.ThrowsAsync<SharedForbiddenException>(() => handler.HandleAsync(new NotificationSendRequestedHostEto()));
        Assert.Empty(scheduler.Requests);
    }

    [Fact]
    public async Task Admission_failure_is_not_swallowed_as_a_successful_send()
    {
        var tenantId = Guid.NewGuid();
        var scheduler = new CapturingScheduler { Failure = new InvalidOperationException("test failure") };
        var handler = new NotificationSendRequestedEventHandler(scheduler, new TestCurrentTenant(tenantId), NullLogger<NotificationSendRequestedEventHandler>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(TenantRequest(tenantId)));
    }

    [Fact]
    public async Task Schedule_and_mode_are_preserved_in_the_admitted_intent()
    {
        var tenantId = Guid.NewGuid();
        var scheduler = new CapturingScheduler();
        var handler = new NotificationSendRequestedEventHandler(scheduler, new TestCurrentTenant(tenantId), NullLogger<NotificationSendRequestedEventHandler>.Instance);
        var message = TenantRequest(tenantId);
        message.ScheduledForUtc = DateTime.UtcNow.AddHours(1);
        message.DeliveryPlan.Mode = NotificationDeliveryMode.OrderedFallback;
        await handler.HandleAsync(message);
        var admitted = Assert.IsType<NotificationSendRequestedTenantEto>(Assert.Single(scheduler.Requests));
        Assert.Equal(message.ScheduledForUtc, admitted.ScheduledForUtc);
        Assert.Equal(NotificationDeliveryMode.OrderedFallback, admitted.DeliveryPlan.Mode);
    }

    private static NotificationSendRequestedTenantEto TenantRequest(Guid tenantId) => new()
    {
        RequestId = Guid.CreateVersion7(),
        TenantId = tenantId,
        DeliveryPlan = new NotificationDeliveryPlan
        {
            Steps = [new NotificationChannelPlan { Channel = NotificationSendChannel.Email, TemplateName = "Tenant.TenantCreated" }]
        },
        Recipients = [new NotificationRecipientPlan
        {
            Key = "recipient", Destinations = [new NotificationDestination
            {
                Channel = NotificationSendChannel.Email, RecipientType = NotificationSendRecipientType.EmailAddress, RecipientValue = "u1@test.local"
            }]
        }]
    };

    private sealed class CapturingScheduler : INotificationDispatchScheduler
    {
        public List<object> Requests { get; } = [];
        public Exception? Failure { get; init; }
        public Task<NotificationDispatchAdmissionDto> ScheduleAsync(NotificationSendRequestedTenantEto input, CancellationToken cancellationToken = default) => Capture(input, input.RequestId);
        public Task<NotificationDispatchAdmissionDto> ScheduleAsync(NotificationSendRequestedHostEto input, CancellationToken cancellationToken = default) => Capture(input, input.RequestId);
        private Task<NotificationDispatchAdmissionDto> Capture(object input, Guid requestId)
        {
            if (Failure is not null) throw Failure;
            Requests.Add(input);
            return Task.FromResult(new NotificationDispatchAdmissionDto(Guid.NewGuid(), requestId, false));
        }
    }
}