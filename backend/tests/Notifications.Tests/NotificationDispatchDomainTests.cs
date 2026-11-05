using Notifications.Domain.Dispatches;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationDispatchDomainTests
{
    internal static readonly DateTime Now = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parallel_plans_activate_every_due_channel_for_each_recipient()
    {
        var dispatch = Dispatch(NotificationDispatchMode.Parallel);
        var first = AddRecipient(dispatch, "first");
        var second = AddRecipient(dispatch, "second");
        Assert.Equal(2, first.EligibleSteps(dispatch.Mode, Now).Count);
        first.Steps.First().Observe(NotificationDispatchState.Delivered);
        Assert.Equal(2, second.EligibleSteps(dispatch.Mode, Now).Count);
    }

    [Theory]
    [InlineData(NotificationDispatchState.Accepted)]
    [InlineData(NotificationDispatchState.Delivered)]
    [InlineData(NotificationDispatchState.Deferred)]
    [InlineData(NotificationDispatchState.NeedsReview)]
    [InlineData(NotificationDispatchState.Queued)]
    public void Accepted_delivered_held_or_uncertain_outcomes_block_fallback(NotificationDispatchState outcome)
    {
        var dispatch = Dispatch(NotificationDispatchMode.OrderedFallback);
        var recipient = AddRecipient(dispatch, "recipient");
        Assert.Single(recipient.EligibleSteps(dispatch.Mode, Now));
        recipient.Steps.First().Observe(outcome);
        Assert.Empty(recipient.EligibleSteps(dispatch.Mode, Now));
    }

    [Fact]
    public void Definitive_failure_advances_only_the_affected_recipient()
    {
        var dispatch = Dispatch(NotificationDispatchMode.OrderedFallback);
        var first = AddRecipient(dispatch, "first");
        var second = AddRecipient(dispatch, "second");
        first.Steps.First().Observe(NotificationDispatchState.Rejected);
        Assert.Equal(NotificationChannel.Sms, Assert.Single(first.EligibleSteps(dispatch.Mode, Now)).Channel);
        Assert.Equal(NotificationChannel.Email, Assert.Single(second.EligibleSteps(dispatch.Mode, Now)).Channel);
    }

    [Fact]
    public void Scheduling_claim_blocks_concurrent_activation_and_expires_without_implying_delivery()
    {
        var dispatch = Dispatch(NotificationDispatchMode.OrderedFallback);
        var recipient = AddRecipient(dispatch, "recipient");
        var step = Assert.Single(recipient.EligibleSteps(dispatch.Mode, Now));
        var claim = step.ClaimScheduling(Now);
        Assert.Empty(recipient.EligibleSteps(dispatch.Mode, Now));
        Assert.True(step.OwnsSchedulingClaim(claim, Now.AddSeconds(59)));
        Assert.False(step.OwnsSchedulingClaim(claim, Now.AddSeconds(60)));
        Assert.False(step.OwnsSchedulingClaim(Guid.NewGuid(), Now));
        Assert.Throws<InvalidOperationException>(() => step.ClaimScheduling(Now));
        step.ReleaseScheduling(Now.AddMinutes(1), "quota_window");
        Assert.Empty(recipient.EligibleSteps(dispatch.Mode, Now));
        Assert.Single(recipient.EligibleSteps(dispatch.Mode, Now.AddMinutes(1)));
    }

    [Fact]
    public void Cancellation_stops_unstarted_work_without_retracting_accepted_evidence()
    {
        var dispatch = Dispatch(NotificationDispatchMode.Parallel);
        var recipient = AddRecipient(dispatch, "recipient");
        var email = recipient.Steps.First();
        email.ClaimScheduling(Now);
        email.MarkScheduled();
        email.Observe(NotificationDispatchState.Accepted);
        dispatch.RequestCancellation(Now, "operator");
        Assert.Equal(NotificationDispatchState.Accepted, email.State);
        Assert.Equal(NotificationDispatchState.Cancelled, recipient.Steps.Last().State);
        Assert.False(dispatch.CanStartWork(Now));
        Assert.Empty(recipient.EligibleSteps(dispatch.Mode, Now));
        var revision = dispatch.Revision;
        dispatch.RequestCancellation(Now.AddSeconds(1), "operator");
        Assert.Equal(revision, dispatch.Revision);
        Assert.Single(dispatch.Events, history => history.Kind == NotificationDispatchEventKind.CancelRequested);
    }

    [Fact]
    public void Contradictory_failure_after_delivery_requires_review()
    {
        var dispatch = Dispatch(NotificationDispatchMode.OrderedFallback);
        var recipient = AddRecipient(dispatch, "recipient");
        var step = recipient.Steps.First();
        step.Observe(NotificationDispatchState.Delivered);
        step.Observe(NotificationDispatchState.Rejected);
        Assert.Equal(NotificationDispatchState.NeedsReview, step.State);
        Assert.Equal("contradictory_delivery_evidence", step.OutcomeReason);
        Assert.Empty(recipient.EligibleSteps(dispatch.Mode, Now));
        dispatch.RefreshSummary();
        Assert.Equal(NotificationDispatchState.NeedsReview, dispatch.State);
    }

    [Fact]
    public void Fallback_success_does_not_wait_for_unused_later_channels()
    {
        var dispatch = Dispatch(NotificationDispatchMode.OrderedFallback);
        var recipient = AddRecipient(dispatch, "recipient");
        recipient.Steps.First().Observe(NotificationDispatchState.Delivered);
        dispatch.RefreshSummary();
        Assert.Equal(NotificationDispatchState.Delivered, dispatch.State);
        Assert.Equal(NotificationDispatchState.Pending, recipient.Steps.Last().State);
    }

    [Fact]
    public void Recipient_and_step_ownership_cannot_cross_tenants_or_dispatches()
    {
        var dispatch = Dispatch(NotificationDispatchMode.Parallel);
        var alien = new NotificationDispatchRecipient(Guid.NewGuid(), Guid.NewGuid(), dispatch.Id, "alien", null);
        Assert.Throws<InvalidOperationException>(() => dispatch.AddRecipient(alien));
        var recipient = AddRecipient(dispatch, "recipient");
        var step = new NotificationDispatchStep(Guid.NewGuid(), Guid.NewGuid(), dispatch.Id, recipient.Id,
            NotificationChannel.Push, 2, "{}", Now);
        Assert.Throws<InvalidOperationException>(() => recipient.AddStep(step));
        Assert.Throws<InvalidOperationException>(() => dispatch.AddRecipient(
            new NotificationDispatchRecipient(Guid.NewGuid(), dispatch.TenantId, dispatch.Id, "recipient", null)));
    }

    [Fact]
    public void Channel_notification_links_once_and_only_inside_its_own_scope()
    {
        var tenantId = Guid.NewGuid();
        var notification = new Notification(Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Email, "test", "{}");
        Assert.Throws<InvalidOperationException>(() => notification.AttachDispatchStep(Guid.NewGuid(), Guid.NewGuid()));
        notification.AttachDispatchStep(Guid.NewGuid(), tenantId);
        Assert.Throws<InvalidOperationException>(() => notification.AttachDispatchStep(Guid.NewGuid(), tenantId));
    }

    [Fact]
    public void Activation_and_expiry_are_enforced_independently_of_cancellation()
    {
        var dispatch = Dispatch(NotificationDispatchMode.Parallel);
        Assert.False(dispatch.CanStartWork(Now.AddTicks(-1)));
        Assert.True(dispatch.CanStartWork(Now));
        Assert.False(dispatch.CanStartWork(dispatch.ExpiresAtUtc));
        Assert.Throws<ArgumentException>(() => new NotificationDispatch(Guid.NewGuid(), null, Guid.NewGuid(), new string('a', 64),
            "test", NotificationDispatchMode.Parallel, Now, Now.AddDays(31)));
    }

    [Theory]
    [InlineData(NotificationDispatchState.Accepted, NotificationDispatchState.Delivered, NotificationDispatchState.Accepted)]
    [InlineData(NotificationDispatchState.Rejected, NotificationDispatchState.Delivered, NotificationDispatchState.PartiallyCompleted)]
    [InlineData(NotificationDispatchState.Suppressed, NotificationDispatchState.Delivered, NotificationDispatchState.Delivered)]
    [InlineData(NotificationDispatchState.NeedsReview, NotificationDispatchState.Delivered, NotificationDispatchState.NeedsReview)]
    public void Aggregate_status_preserves_partial_and_unconfirmed_outcomes(
        NotificationDispatchState first, NotificationDispatchState second, NotificationDispatchState expected) =>
        Assert.Equal(expected, NotificationDispatchOutcomes.Aggregate([first, second]));

    internal static NotificationDispatch Dispatch(NotificationDispatchMode mode, Guid? tenantId = null, Guid? requestId = null) =>
        new(Guid.CreateVersion7(), tenantId, requestId ?? Guid.CreateVersion7(), new string('a', 64), "test", mode, Now, Now.AddDays(7));

    internal static NotificationDispatchRecipient AddRecipient(NotificationDispatch dispatch, string key)
    {
        var recipient = new NotificationDispatchRecipient(Guid.CreateVersion7(), dispatch.TenantId, dispatch.Id, key, null);
        recipient.AddStep(new NotificationDispatchStep(Guid.CreateVersion7(), dispatch.TenantId, dispatch.Id, recipient.Id,
            NotificationChannel.Email, 0, "{}", Now));
        recipient.AddStep(new NotificationDispatchStep(Guid.CreateVersion7(), dispatch.TenantId, dispatch.Id, recipient.Id,
            NotificationChannel.Sms, 1, "{}", Now));
        dispatch.AddRecipient(recipient);
        return recipient;
    }
}
