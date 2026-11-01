using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Jobs;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

public class BackgroundJobRegistryTests
{
    [Fact]
    public void Ctor_WhenDuplicateJobName_Throws()
    {
        var options = new BackgroundTasksOptions();
        options.Jobs.Add(BackgroundJobDefinition.Create<TestJob, TestArgs>("same-name"));
        options.Jobs.Add(BackgroundJobDefinition.Create<OtherTestJob, OtherArgs>("same-name"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new BackgroundJobRegistry(Options.Create(options)));

        Assert.Contains("Duplicate background job name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequired_WhenRegisteredByArgsType_ReturnsDefinition()
    {
        var options = new BackgroundTasksOptions();
        options.Jobs.Add(BackgroundJobDefinition.Create<TestJob, TestArgs>("test-job"));
        var registry = new BackgroundJobRegistry(Options.Create(options));

        var definition = registry.GetRequired<TestArgs>();

        Assert.Equal("test-job", definition.Name);
        Assert.Equal(typeof(TestJob), definition.JobType);
    }

    private sealed record TestArgs(string Value);
    private sealed record OtherArgs(string Value);

    private sealed class TestJob : IBackgroundJob<TestArgs>
    {
        public Task ExecuteAsync(BackgroundJobExecutionContext<TestArgs> context) => Task.CompletedTask;
    }

    private sealed class OtherTestJob : IBackgroundJob<OtherArgs>
    {
        public Task ExecuteAsync(BackgroundJobExecutionContext<OtherArgs> context) => Task.CompletedTask;
    }
}
