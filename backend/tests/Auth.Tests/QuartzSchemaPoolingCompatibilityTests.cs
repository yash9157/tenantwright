using Microsoft.Extensions.Configuration;
using Xunit;

namespace Auth.Tests;

public sealed class QuartzSchemaPoolingCompatibilityTests
{
    [Fact]
    public void Transaction_pool_rejects_runtime_quartz_schema_creation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgreSql:Runtime:Mode"] = "TransactionPool"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            QuartzSchemaInitializer.ValidateRuntimeConfiguration(
                configuration,
                usePersistentStore: true,
                autoCreateSchema: true));

        Assert.Contains("Auth:Quartz:AutoCreateSchema", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Direct", true, true)]
    [InlineData("SessionPool", true, true)]
    [InlineData("TransactionPool", true, false)]
    [InlineData("TransactionPool", false, true)]
    public void Compatible_runtime_modes_do_not_reject_schema_configuration(
        string mode,
        bool usePersistentStore,
        bool autoCreateSchema)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgreSql:Runtime:Mode"] = mode
            })
            .Build();

        QuartzSchemaInitializer.ValidateRuntimeConfiguration(
            configuration,
            usePersistentStore,
            autoCreateSchema);
    }
}
