using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Shared.BackgroundTasks.Hangfire;
using Shared.BackgroundTasks.Jobs;
using Xunit;

namespace Shared.BackgroundTasks.Hangfire.Tests;

public class HangfireBackgroundJobManagerTests
{
    [Fact]
    public async Task EnqueueAsync_UsesStableDispatcherEnvelope()
    {
        var client = new FakeBackgroundJobClient();
        var envelope = new BackgroundJobEnvelope
        {
            JobName = "test-job",
            ArgsJson = "{}",
            Queue = "test-default"
        };
        var manager = new HangfireBackgroundJobManager(
            client,
            new FakeEnvelopeFactory(envelope));

        var jobId = await manager.EnqueueAsync(new TestArgs("ok"));

        Assert.Equal("job-1", jobId);
        Assert.Equal(typeof(HangfireBackgroundJobDispatcher), client.CreatedJob!.Type);
        Assert.Equal(nameof(HangfireBackgroundJobDispatcher.DispatchAsync), client.CreatedJob.Method.Name);
        Assert.IsType<EnqueuedState>(client.CreatedState);
    }

    [Fact]
    public async Task AddOrUpdateAsync_RegistersStableDispatcherRecurringJob()
    {
        var recurringJobManager = new FakeRecurringJobManager();
        var envelope = new BackgroundJobEnvelope
        {
            JobName = "test-job",
            ArgsJson = "{}",
            Queue = "Test Recurring"
        };
        var manager = new HangfireRecurringBackgroundJobManager(
            recurringJobManager,
            new FakeEnvelopeFactory(envelope));

        await manager.AddOrUpdateAsync(
            " nightly ",
            new TestArgs("ok"),
            "*/5 * * * *",
            new RecurringBackgroundJobOptions { TimeZone = TimeZoneInfo.Utc });

        Assert.Equal("nightly", recurringJobManager.RecurringJobId);
        Assert.Equal("*/5 * * * *", recurringJobManager.CronExpression);
        Assert.Equal(typeof(HangfireBackgroundJobDispatcher), recurringJobManager.Job!.Type);
        Assert.Equal(nameof(HangfireBackgroundJobDispatcher.DispatchAsync), recurringJobManager.Job.Method.Name);
#pragma warning disable CS0618
        Assert.Equal("test_recurring", recurringJobManager.Options!.QueueName);
#pragma warning restore CS0618
    }

    private sealed record TestArgs(string Value);

    private sealed class FakeEnvelopeFactory : IBackgroundJobEnvelopeFactory
    {
        private readonly BackgroundJobEnvelope _envelope;

        public FakeEnvelopeFactory(BackgroundJobEnvelope envelope)
        {
            _envelope = envelope;
        }

        public BackgroundJobEnvelope Create<TArgs>(TArgs args, BackgroundJobEnqueueOptions? options = null) => _envelope;

        public BackgroundJobEnvelope CreateRecurring<TArgs>(TArgs args, RecurringBackgroundJobOptions? options = null) => _envelope;
    }

    private sealed class FakeBackgroundJobClient : IBackgroundJobClient
    {
        public Job? CreatedJob { get; private set; }
        public IState? CreatedState { get; private set; }

        public string Create(Job job, IState state)
        {
            CreatedJob = job;
            CreatedState = state;
            return "job-1";
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private sealed class FakeRecurringJobManager : IRecurringJobManager
    {
        public string? RecurringJobId { get; private set; }
        public Job? Job { get; private set; }
        public string? CronExpression { get; private set; }
        public RecurringJobOptions? Options { get; private set; }
        public string? RemovedJobId { get; private set; }
        public string? TriggeredJobId { get; private set; }

        public void AddOrUpdate(
            string recurringJobId,
            Job job,
            string cronExpression,
            RecurringJobOptions options)
        {
            RecurringJobId = recurringJobId;
            Job = job;
            CronExpression = cronExpression;
            Options = options;
        }

        public void RemoveIfExists(string recurringJobId)
        {
            RemovedJobId = recurringJobId;
        }

        public void Trigger(string recurringJobId)
        {
            TriggeredJobId = recurringJobId;
        }
    }
}
