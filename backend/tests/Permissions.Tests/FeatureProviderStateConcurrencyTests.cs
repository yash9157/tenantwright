using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Permissions.Domain.FeatureManagement;
using Permissions.Tests.TestDoubles;
using Xunit;

namespace Permissions.Tests;

public sealed class FeatureProviderStateConcurrencyTests
{
    [Fact]
    public async Task Stale_provider_snapshot_cannot_commit_values_on_a_different_feature_row()
    {
        var tenantId = Guid.CreateVersion7();
        var providerKey = tenantId.ToString("D");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setup = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.FeatureProviderStates.Add(new FeatureProviderState(
                Guid.CreateVersion7(), tenantId, "Feature", providerKey));
            setup.FeatureValues.AddRange(
                new FeatureValue(Guid.CreateVersion7(), "Example.Base", "true", "T", providerKey, tenantId),
                new FeatureValue(Guid.CreateVersion7(), "Example.Dependent", "false", "T", providerKey, tenantId));
            await setup.SaveChangesAsync();
        }

        await using var firstWriter = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId);
        await using var staleWriter = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId);

        var firstState = await firstWriter.FeatureProviderStates.SingleAsync();
        var staleState = await staleWriter.FeatureProviderStates.SingleAsync();
        var prerequisite = await firstWriter.FeatureValues.SingleAsync(value => value.Name == "Example.Base");
        var dependent = await staleWriter.FeatureValues.SingleAsync(value => value.Name == "Example.Dependent");

        prerequisite.SetValue("false");
        firstState.Advance(DateTime.UtcNow);
        dependent.SetValue("true");
        staleState.Advance(DateTime.UtcNow);

        await firstWriter.SaveChangesAsync();
        await Assert.ThrowsAsync<Shared.Data.Exceptions.SharedDbConcurrencyException>(() => staleWriter.SaveChangesAsync());

        await using var verification = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId);
        var persisted = await verification.FeatureValues
            .AsNoTracking()
            .ToDictionaryAsync(value => value.Name, value => value.Value);
        var durableState = await verification.FeatureProviderStates.AsNoTracking().SingleAsync();

        Assert.Equal("false", persisted["Example.Base"]);
        Assert.Equal("false", persisted["Example.Dependent"]);
        Assert.Equal(1, durableState.Stamp);
    }
}
