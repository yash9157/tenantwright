using Notifications.Application.Delivery;
using Notifications.Domain.Notifications;
using Xunit;

namespace Notifications.Tests;

public sealed class TenantNotificationSuppressionCheckerTests
{
    [Fact]
    public async Task Checker_reads_only_the_explicit_tenant_suppression()
    {
        var suppressedTenantId = Guid.CreateVersion7();
        var otherTenantId = Guid.CreateVersion7();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            $"suppression-checker-{Guid.NewGuid():N}",
            tenantId: null);
        dbContext.TenantNotificationSuppressions.Add(new TenantNotificationSuppression
        {
            TenantId = suppressedTenantId,
            IsSuppressed = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var checker = new TenantNotificationSuppressionChecker(
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext));

        Assert.True(await checker.IsSuppressedAsync(suppressedTenantId));
        Assert.False(await checker.IsSuppressedAsync(otherTenantId));
    }
}
