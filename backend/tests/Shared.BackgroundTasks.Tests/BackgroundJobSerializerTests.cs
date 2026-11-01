using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Jobs;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

public class BackgroundJobSerializerTests
{
    [Fact]
    public void SerializeArgs_WhenPayloadExceedsLimit_Throws()
    {
        var options = new BackgroundTasksOptions { MaxPayloadBytes = 16 };
        var serializer = new SystemTextJsonBackgroundJobSerializer(Options.Create(options));

        var exception = Assert.Throws<InvalidOperationException>(
            () => serializer.SerializeArgs(new TestArgs(new string('x', 128))));

        Assert.Contains("exceeds the configured limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeArgs_WhenUnsupportedType_Throws()
    {
        var serializer = new SystemTextJsonBackgroundJobSerializer(Options.Create(new BackgroundTasksOptions()));

        var exception = Assert.Throws<InvalidOperationException>(
            () => serializer.SerializeArgs(typeof(string)));

        Assert.Contains("is not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeArgs_WhenValidJson_ReturnsDto()
    {
        var serializer = new SystemTextJsonBackgroundJobSerializer(Options.Create(new BackgroundTasksOptions()));

        var result = Assert.IsType<TestArgs>(
            serializer.DeserializeArgs("{\"value\":\"ok\"}", typeof(TestArgs)));

        Assert.Equal("ok", result.Value);
    }

    private sealed record TestArgs(string Value);
}
