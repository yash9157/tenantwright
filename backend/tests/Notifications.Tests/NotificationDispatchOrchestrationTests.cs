using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Application.Dispatches;
using Notifications.Domain.Digests;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.EventContracts;
using Shared.BackgroundTasks.Workers;
using Xunit;

namespace Notifications.Tests;

[Collection(NotificationPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class NotificationDispatchOrchestrationTests(NotificationPostgresFixture infrastructure)
{
    [Fact]
    public async Task Admission_is_immutable_idempotent_and_scoped_before_any_preparation()
    {
        await using var fixture = await FixtureAsync();
        var input = Request(fixture.TenantId);
        var admitted = await fixture.Admission.ScheduleAsync(input);
        var duplicate = await fixture.Admission.ScheduleAsync(input);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(admitted.DispatchId, duplicate.DispatchId);
        Assert.Empty(await fixture.Context.Notifications.ToListAsync());
        Assert.Equal(2, await fixture.Context.NotificationDispatchRecipients.CountAsync());
        Assert.Equal(4, await fixture.Context.NotificationDispatchSteps.CountAsync());
        input.DeliveryPlan.Steps[0].TemplateModel["changed"] = "new content";
        await Assert.ThrowsAsync<SharedConflictException>(() => fixture.Admission.ScheduleAsync(input));
        input.TenantId = Guid.NewGuid();
        await Assert.ThrowsAsync<SharedForbiddenException>(() => fixture.Admission.ScheduleAsync(input));
    }

    [Fact]
    public async Task Fallback_is_per_recipient_and_acceptance_holds_the_other_route()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Admission.ScheduleAsync(Request(fixture.TenantId));
        fixture.Scheduler.Outcome = address => address == "a@example.test" ? NotificationStatus.DeadLettered : NotificationStatus.ProviderAccepted;
        await fixture.Coordinator.ProcessBatchAsync();
        Assert.Equal(2, fixture.Scheduler.Calls.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Coordinator.ProcessBatchAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Coordinator.ProcessBatchAsync();
        Assert.Equal(3, fixture.Scheduler.Calls.Count);
        Assert.Equal("+12025550101", fixture.Scheduler.Calls.Last());
        Assert.DoesNotContain("+12025550102", fixture.Scheduler.Calls);
    }

    [Fact]
    public async Task Contradictory_receipt_fences_a_queued_fallback_before_reconciliation_runs()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Admission.ScheduleAsync(Request(fixture.TenantId));
        fixture.Scheduler.Outcome = address => address == "a@example.test" ? NotificationStatus.DeadLettered : NotificationStatus.Queued;
        await fixture.Coordinator.ProcessBatchAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Coordinator.ProcessBatchAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Coordinator.ProcessBatchAsync();
        var fallback = await fixture.Context.Notifications.SingleAsync(child => child.Channel == NotificationChannel.Sms);
        var earlierStep = await fixture.Context.NotificationDispatchSteps.SingleAsync(step =>
            step.RecipientId == fixture.Context.NotificationDispatchSteps.Where(other => other.Id == fallback.DispatchStepId)
                .Select(other => other.RecipientId).Single() && step.Ordinal == 0);
        var earlier = await fixture.Context.Notifications.SingleAsync(child => child.DispatchStepId == earlierStep.Id);
        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        await NotificationDispatchGate.DirtyAsync(fixture.Context, earlier, now, default, contradictoryReceipt: true);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(NotificationDispatchState.NeedsReview,
            await NotificationDispatchGate.ClaimSendAsync(fixture.Context, fallback, now, default));
    }

    [Fact]
    public async Task Routed_digest_cannot_be_cloned_or_retried_independently()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Admission.ScheduleAsync(Request(fixture.TenantId));
        fixture.Scheduler.Outcome = _ => NotificationStatus.Queued;
        await fixture.Coordinator.ProcessBatchAsync();
        var source = await fixture.Context.Notifications.FirstAsync();
        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        var bucket = new NotificationDigestBucket(Guid.NewGuid(), fixture.TenantId, source.Channel,
            NotificationRecipientType.EmailAddress, "a@example.test", NotificationDigestFrequency.Daily, now.AddDays(-1), now);
        var emitted = new Notification(Guid.NewGuid(), fixture.TenantId, bucket.Id, source.Channel, "digest", "{}",
            sourceService: NotificationDispatchRecoveryGate.DigestSourceService);
        bucket.MarkEmitted(emitted.Id, now);
        fixture.Context.AddRange(bucket, emitted, new NotificationDigestItem(0, fixture.TenantId, bucket.Id, source.Id, "digest", null, now));
        await fixture.Context.SaveChangesAsync();

        var repository = new EfTestRepository<Notification, Guid>(fixture.Context);
        await Assert.ThrowsAsync<SharedConflictException>(() => NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(repository, emitted, default));
        await Assert.ThrowsAsync<SharedConflictException>(() => NotificationDispatchRecoveryGate.ApplyAsync(repository, emitted, true, now, "operator", default));
        var owner = Assert.Single(await NotificationDispatchGate.ForNotificationAsync(fixture.Context, emitted, default)).Dispatch;
        var revision = owner.Revision;
        await NotificationDispatchRecoveryGate.ApplyAsync(repository, emitted, false, now, "operator", default);
        Assert.True(owner.Revision > revision);
    }

    [Fact]
    public async Task Parallel_dispatch_queues_every_explicit_channel_but_preparation_never_holds_a_transaction()
    {
        await using var fixture = await FixtureAsync();
        var input = Request(fixture.TenantId);
        input.DeliveryPlan.Mode = NotificationDeliveryMode.Parallel;
        await fixture.Admission.ScheduleAsync(input);
        await fixture.Coordinator.ProcessBatchAsync();
        Assert.Equal(4, fixture.Scheduler.Calls.Count);
        Assert.Equal(4, await fixture.Context.Notifications.CountAsync());
        Assert.All(await fixture.Context.Notifications.ToListAsync(), child => Assert.Equal(child.RequestId, child.DispatchStepId));
    }

    [Fact]
    public async Task Cancellation_during_preparation_fences_child_creation()
    {
        await using var fixture = await FixtureAsync();
        var admitted = await fixture.Admission.ScheduleAsync(Request(fixture.TenantId));
        fixture.Scheduler.BeforeLink = async () =>
        {
            var dispatch = await fixture.Context.NotificationDispatches.Include(dispatch => dispatch.Recipients)
                .ThenInclude(recipient => recipient.Steps).SingleAsync(dispatch => dispatch.Id == admitted.DispatchId);
            dispatch.RequestCancellation(fixture.Clock.GetUtcNow().UtcDateTime, "operator");
            await fixture.Context.SaveChangesAsync();
        };
        await fixture.Coordinator.ProcessBatchAsync();
        Assert.Empty(await fixture.Context.Notifications.ToListAsync());
        Assert.NotNull((await fixture.Context.NotificationDispatches.SingleAsync()).CancelRequestedAtUtc);
        Assert.All(await fixture.Context.NotificationDispatchSteps.ToListAsync(), step => Assert.Equal(NotificationDispatchState.Cancelled, step.State));
    }

    [Fact]
    public async Task Review_and_digest_policy_holds_never_activate_a_fallback_channel()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Admission.ScheduleAsync(Request(fixture.TenantId));
        fixture.Scheduler.Outcome = _ => NotificationStatus.NeedsReview;
        await fixture.Coordinator.ProcessBatchAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Coordinator.ProcessBatchAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Coordinator.ProcessBatchAsync();
        Assert.Equal(2, fixture.Scheduler.Calls.Count);
        Assert.Equal(NotificationDispatchState.NeedsReview, (await fixture.Context.NotificationDispatches.SingleAsync()).State);
    }

    [Fact]
    public async Task Admission_persists_the_fingerprinted_snapshot_when_the_callers_dto_changes_during_await()
    {
        await using var fixture = await FixtureAsync();
        var request = Request(fixture.TenantId);
        request.DeliveryPlan.Steps[0].TemplateModel["value"] = "original";
        fixture.BeforeContextAccess = () =>
        {
            request.DeliveryPlan.Steps[0].TemplateModel["value"] = "changed";
            request.Recipients[0].Key = "changed-person";
            fixture.BeforeContextAccess = null;
        };
        await fixture.Admission.ScheduleAsync(request);
        var recipient = await fixture.Context.NotificationDispatchRecipients.Include(recipient => recipient.Steps)
            .SingleAsync(recipient => recipient.RecipientKey == "a");
        Assert.DoesNotContain("changed", recipient.Steps.Single(step => step.Channel == NotificationChannel.Email).ContentJson, StringComparison.Ordinal);
        Assert.Contains("original", recipient.Steps.Single(step => step.Channel == NotificationChannel.Email).ContentJson, StringComparison.Ordinal);
        request.DeliveryPlan.Steps[0].TemplateModel["value"] = "original";
        request.Recipients[0].Key = "a";
        Assert.True((await fixture.Admission.ScheduleAsync(request)).IsDuplicate);
    }

    private async Task<Fixture> FixtureAsync()
    {
        var connection = await infrastructure.CreateDatabaseAsync();
        var tenantId = Guid.NewGuid();
        var context = NotificationsEfCoreTestHarness.BuildPostgresDbContext(connection, tenantId, true);
        await context.Database.EnsureCreatedAsync();
        return new Fixture(context, tenantId);
    }

    [Fact]
    public async Task Deadline_expires_every_unstarted_route_without_scheduling_a_child()
    {
        await using var fixture = await FixtureAsync();
        var request = Request(fixture.TenantId);
        request.ExpiresAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(1);
        await fixture.Admission.ScheduleAsync(request);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.ProcessBatchAsync();
        Assert.Empty(fixture.Scheduler.Calls);
        Assert.All(await fixture.Context.NotificationDispatchSteps.ToListAsync(), step => Assert.Equal(NotificationDispatchState.Expired, step.State));
        Assert.Equal(NotificationDispatchState.Expired, (await fixture.Context.NotificationDispatches.SingleAsync()).State);
    }

    internal static NotificationSendRequestedTenantEto Request(Guid tenantId) => new()
    {
        TenantId = tenantId,
        RequestId = Guid.CreateVersion7(),
        SourceService = "notifications-tests",
        DeliveryPlan = new NotificationDeliveryPlan
        {
            Mode = NotificationDeliveryMode.OrderedFallback,
            Steps = [new() { Channel = NotificationSendChannel.Email, TemplateName = "test" },
                new() { Channel = NotificationSendChannel.Sms, TemplateName = "test" }]
        },
        Recipients = [Recipient("a", "a@example.test", "+12025550101"), Recipient("b", "b@example.test", "+12025550102")]
    };

    private static NotificationRecipientPlan Recipient(string key, string email, string phone) => new()
    {
        Key = key,
        Destinations = [new() { Channel = NotificationSendChannel.Email, RecipientType = NotificationSendRecipientType.EmailAddress, RecipientValue = email },
            new() { Channel = NotificationSendChannel.Sms, RecipientType = NotificationSendRecipientType.PhoneNumber, RecipientValue = phone }]
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public NotificationsDbContextApp Context { get; }
        public Guid TenantId { get; }
        public DispatchClock Clock { get; } = new();
        public NotificationDispatchScheduler Admission { get; }
        public NotificationDispatchCoordinator Coordinator { get; }
        public FencedScheduler Scheduler { get; }
        public Action? BeforeContextAccess { get; set; }

        public Fixture(NotificationsDbContextApp context, Guid tenantId)
        {
            Context = context;
            TenantId = tenantId;
            var currentTenant = new TestCurrentTenant(tenantId);
            var provider = new CallbackContextProvider(context, () => BeforeContextAccess?.Invoke());
            var options = Options.Create(new NotificationDispatchOptions());
            Admission = new NotificationDispatchScheduler(new NoopUnitOfWorkManager(), provider, currentTenant, Clock,
                options, Options.Create(new NotificationsDeliveryOptions()), new DispatchWakeSignal(), NullLogger<NotificationDispatchScheduler>.Instance);
            Scheduler = new FencedScheduler(context, Clock);
            Coordinator = new NotificationDispatchCoordinator(new RelationalUnitOfWorkExecutor(context), provider, Scheduler,
                currentTenant, Clock, options, NullLogger<NotificationDispatchCoordinator>.Instance);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class CallbackContextProvider(NotificationsDbContextApp context, Action beforeAccess) : IDbContextProvider<NotificationsDbContextApp>
    {
        public Task<NotificationsDbContextApp> GetDbContextAsync()
        {
            beforeAccess();
            return Task.FromResult(context);
        }
    }

    private sealed class FencedScheduler(NotificationsDbContextApp context, DispatchClock clock) : INotificationDeliveryScheduler
    {
        public List<string> Calls { get; } = [];
        public Func<string, NotificationStatus> Outcome { get; set; } = _ => NotificationStatus.ProviderAccepted;
        public Func<Task>? BeforeLink { get; set; }

        public Task<NotificationScheduleResult> ScheduleAsync(NotificationChannelRequestHost input, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Tenant work must not use the host channel contract.");

        public async Task<NotificationScheduleResult> ScheduleAsync(NotificationChannelRequestTenant input, CancellationToken cancellationToken = default)
        {
            Assert.Null(context.Database.CurrentTransaction);
            if (BeforeLink is not null)
                await BeforeLink();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var child = new Notification(Guid.CreateVersion7(), input.TenantId, input.RequestId,
                (NotificationChannel)(int)input.Channel, input.TemplateName, "{}");
            await NotificationDispatchGate.LinkAsync(context, child, input.DispatchStepId!.Value,
                input.SchedulingClaimId!.Value, clock.GetUtcNow().UtcDateTime, cancellationToken);
            context.Notifications.Add(child);
            var destination = Assert.Single(input.Recipients);
            var recipient = new NotificationRecipient(0, input.TenantId, child.Id,
                (NotificationRecipientType)(int)destination.RecipientType, destination.RecipientValue);
            context.NotificationRecipients.Add(recipient);
            await context.SaveChangesAsync(cancellationToken);
            var delivery = new NotificationDelivery(0, input.TenantId, child.Id, recipient.Id);
            var outcome = Outcome(destination.RecipientValue);
            if (outcome == NotificationStatus.ProviderAccepted)
                delivery.MarkProviderAccepted(clock.GetUtcNow().UtcDateTime, null);
            else if (outcome == NotificationStatus.DeadLettered)
                delivery.MarkDeadLettered("definitive_rejection", clock.GetUtcNow().UtcDateTime);
            else if (outcome == NotificationStatus.NeedsReview)
                delivery.MarkNeedsReview("ambiguous", clock.GetUtcNow().UtcDateTime);
            context.NotificationDeliveries.Add(delivery);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Calls.Add(destination.RecipientValue);
            return new NotificationScheduleResult { Accepted = true, RequestId = input.RequestId, NotificationId = child.Id, QueuedCount = 1 };
        }
    }

    private sealed class DispatchClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class DispatchWakeSignal : IBackgroundWorkerWakeSignal
    {
        public Task SignalAsync(string key, BackgroundWorkerWakeOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BackgroundWorkerWakeResult> WaitAsync(string key, BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(BackgroundWorkerWakeResult.SafetyPollTimeout);
    }
}
