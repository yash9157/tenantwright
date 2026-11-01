using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Jobs;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

public class BackgroundJobEnvelopeFactoryTests
{
    [Fact]
    public void Create_WhenArgsCarryTenant_UsesArgsTenantAndPreservesMetadata()
    {
        var tenantId = Guid.NewGuid();
        var requestedBy = Guid.NewGuid();
        var factory = CreateFactory();

        var envelope = factory.Create(
            new TenantArgs(tenantId, "payload"),
            new BackgroundJobEnqueueOptions
            {
                CorrelationId = "corr-1",
                RequestedByUserId = requestedBy,
                Queue = "critical"
            });

        Assert.Equal("tenant-job", envelope.JobName);
        Assert.Equal(tenantId, envelope.TenantId);
        Assert.Equal("corr-1", envelope.CorrelationId);
        Assert.Equal(requestedBy, envelope.RequestedByUserId);
        Assert.Equal("critical", envelope.Queue);
        Assert.Contains("payload", envelope.ArgsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WhenTenantArgsMissingTenant_Throws()
    {
        var factory = CreateFactory();

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.Create(new TenantArgs(null, "payload")));

        Assert.Contains("must include TenantId", exception.Message, StringComparison.Ordinal);
    }

    private static BackgroundJobEnvelopeFactory CreateFactory()
    {
        var options = new BackgroundTasksOptions();
        options.Jobs.Add(BackgroundJobDefinition.Create<TenantJob, TenantArgs>("tenant-job"));
        return new BackgroundJobEnvelopeFactory(
            new BackgroundJobRegistry(Options.Create(options)),
            new SystemTextJsonBackgroundJobSerializer(Options.Create(new BackgroundTasksOptions())),
            new FakeCurrentTenant(null));
    }

    private sealed record TenantArgs(Guid? TenantId, string Value) : IBackgroundJobTenantArgs;

    private sealed class TenantJob : IBackgroundJob<TenantArgs>
    {
        public Task ExecuteAsync(BackgroundJobExecutionContext<TenantArgs> context) => Task.CompletedTask;
    }

    private sealed class FakeCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;

        public FakeCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new DisposeAction(() => Id = previous);
        }
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _action;

        public DisposeAction(Action action)
        {
            _action = action;
        }

        public void Dispose() => _action();
    }
}
