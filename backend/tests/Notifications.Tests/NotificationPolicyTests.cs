using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Policy;
using Notifications.Application.Policy;
using Notifications.Controllers.Policy;
using Notifications.Domain.Policy;
using Notifications.Domain.Shared.Enums;
using Permissions.Application.Contracts.Definitions;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Notifications;
using Xunit;

namespace Notifications.Tests;

public class NotificationPolicyTests
{
    [Fact]
    public void Policy_controller_uses_the_notifications_gateway_prefix()
    {
        Assert.Equal("api/notifications/policy",
            typeof(NotificationPolicyController).GetCustomAttribute<RouteAttribute>()?.Template);
    }

    [Fact]
    public void Permission_catalog_contains_sensitive_query_and_policy_boundaries_without_email_coupling()
    {
        var context = new PermissionDefinitionContext();
        new NotificationsPermissionDefinitionProvider().Define(context);
        var permissions = Flatten(context.Groups[NotificationsPermissions.GroupName].Permissions).ToDictionary(x => x.Name);

        Assert.Contains(NotificationsPermissions.Query.ReadSensitivePayload, permissions.Keys);
        Assert.Contains(NotificationsPermissions.Policy.Read, permissions.Keys);
        Assert.Contains(NotificationsPermissions.Policy.Manage, permissions.Keys);
        Assert.Empty(permissions[NotificationsPermissions.Config.TestSend].RequiredFeatures);
        Assert.Empty(permissions[NotificationsPermissions.Providers.Read].RequiredFeatures);
        Assert.Empty(permissions[NotificationsPermissions.Providers.ActivityRead].RequiredFeatures);
        Assert.Empty(permissions[NotificationsPermissions.Providers.AnalyticsRead].RequiredFeatures);
    }

    [Fact]
    public void Policy_controller_pairs_host_reads_and_mutations_with_exact_permissions_and_auditing()
    {
        AssertActionPolicy(
            nameof(NotificationPolicyController.GetTenantAsync),
            NotificationsPermissions.Policy.Read,
            "tenants/{tenantId:guid}",
            audited: true);
        AssertActionPolicy(
            nameof(NotificationPolicyController.UpdateTenantAsync),
            NotificationsPermissions.Policy.Manage,
            "tenants/{tenantId:guid}",
            audited: true);
        AssertActionPolicy(
            nameof(NotificationPolicyController.PreviewTenantAsync),
            NotificationsPermissions.Policy.Read,
            "tenants/preview",
            audited: true);
    }

    [Fact]
    public async Task Host_tenant_policy_api_fails_closed_when_ambient_tenant_exists()
    {
        var ambientTenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Host_tenant_policy_api_fails_closed_when_ambient_tenant_exists),
            ambientTenantId);
        var service = CreateService(dbContext, ambientTenantId, "operator-1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetTenantPolicyAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateTenantPolicyAsync(Guid.NewGuid(), new UpdateNotificationPolicyInput()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.PreviewTenantPolicyAsync(new NotificationPolicyPreviewInput
            {
                TenantId = Guid.NewGuid(),
                AtUtc = DateTime.UtcNow
            }));
    }

    [Fact]
    public async Task Self_service_preference_derives_user_from_principal_and_may_only_narrow_tenant_floor()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Self_service_preference_derives_user_from_principal_and_may_only_narrow_tenant_floor),
            tenantId);
        var tenantPolicy = new TenantNotificationPolicy(Guid.NewGuid(), tenantId);
        tenantPolicy.Update(
            "Asia/Kolkata",
            quietHoursEnabled: true,
            quietStartLocalMinutes: 22 * 60,
            quietEndLocalMinutes: 7 * 60,
            NotificationPolicyChannel.Email,
            ["Security"],
            NotificationDigestFrequency.Daily);
        dbContext.TenantNotificationPolicies.Add(tenantPolicy);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, tenantId, "principal-user");
        var saved = await service.UpdateMyPreferenceAsync(new UpdateUserNotificationPreferenceInput
        {
            QuietHoursEnabled = true,
            IanaTimeZoneId = "Asia/Kolkata",
            QuietStartLocalMinutes = 21 * 60,
            QuietEndLocalMinutes = 8 * 60,
            QuietChannels = NotificationPolicyChannel.Email | NotificationPolicyChannel.Push,
            DigestFrequency = NotificationDigestFrequency.Weekly,
            ExpectedRevision = 0,
            IdempotencyKey = Guid.NewGuid().ToString("D")
        });

        Assert.Equal(tenantId, saved.TenantId);
        var entity = await dbContext.UserNotificationPreferences.SingleAsync();
        Assert.Equal("principal-user", entity.UserId);
        Assert.Equal(NotificationDigestFrequency.Weekly, entity.DigestFrequency);

        var exception = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.UpdateMyPreferenceAsync(new UpdateUserNotificationPreferenceInput
            {
                QuietHoursEnabled = true,
                IanaTimeZoneId = "Asia/Kolkata",
                QuietStartLocalMinutes = 22 * 60,
                QuietEndLocalMinutes = 7 * 60,
                QuietChannels = NotificationPolicyChannel.Push,
                DigestFrequency = NotificationDigestFrequency.Daily,
                ExpectedRevision = saved.Revision,
                IdempotencyKey = Guid.NewGuid().ToString("D")
            }));
        Assert.Equal("notification_quiet_hours_floor_required", exception.Code);
    }

    [Fact]
    public async Task Tenant_user_cannot_enable_digest_without_tenant_digest_policy()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Tenant_user_cannot_enable_digest_without_tenant_digest_policy),
            tenantId);
        var service = CreateService(dbContext, tenantId, "principal-user");

        var exception = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.UpdateMyPreferenceAsync(new UpdateUserNotificationPreferenceInput
            {
                IanaTimeZoneId = "UTC",
                DigestFrequency = NotificationDigestFrequency.Daily,
                ExpectedRevision = 0,
                IdempotencyKey = Guid.NewGuid().ToString("D")
            }));
        Assert.Equal("notification_digest_not_allowed", exception.Code);
    }

    [Fact]
    public async Task Host_policy_update_is_revision_fenced_and_replays_its_receipt()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Host_policy_update_is_revision_fenced_and_replays_its_receipt),
            tenantId: null);
        var service = CreateService(dbContext, tenantId: null, "operator-1");
        var input = new UpdateNotificationPolicyInput
        {
            IanaTimeZoneId = "Asia/Kolkata",
            QuietHoursEnabled = true,
            QuietStartLocalMinutes = 22 * 60,
            QuietEndLocalMinutes = 7 * 60,
            QuietChannels = NotificationPolicyChannel.Email,
            DigestFrequency = NotificationDigestFrequency.Daily,
            ExpectedRevision = 0,
            IdempotencyKey = Guid.NewGuid().ToString("D")
        };

        var applied = await service.UpdateTenantPolicyAsync(tenantId, input);
        var replay = await service.UpdateTenantPolicyAsync(tenantId, input);

        Assert.Equal(1, applied.Revision);
        Assert.NotNull(applied.Receipt);
        Assert.False(applied.Receipt.IsReplay);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Equal(applied.Receipt.ReceiptId, replay.Receipt.ReceiptId);
        Assert.Single(dbContext.NotificationOperationReceipts);

        var stale = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.UpdateTenantPolicyAsync(tenantId, new UpdateNotificationPolicyInput
            {
                IanaTimeZoneId = "UTC",
                ExpectedRevision = 0,
                IdempotencyKey = Guid.NewGuid().ToString("D")
            }));
        Assert.Equal("notification_revision_conflict", stale.Code);
    }

    [Fact]
    public void Time_zone_validation_rejects_windows_ids_and_accepts_iana_ids()
    {
        var policy = new TenantNotificationPolicy(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => policy.Update(
            "India Standard Time",
            false,
            0,
            0,
            NotificationPolicyChannel.None,
            [],
            NotificationDigestFrequency.Disabled));

        policy.Update(
            "Asia/Kolkata",
            false,
            0,
            0,
            NotificationPolicyChannel.None,
            [],
            NotificationDigestFrequency.Disabled);
        Assert.Equal("Asia/Kolkata", policy.IanaTimeZoneId);
    }

    [Fact]
    public void Cross_midnight_quiet_hours_hold_until_local_end()
    {
        var policy = CreatePolicy(
            "America/New_York",
            startMinutes: 22 * 60,
            endMinutes: 7 * 60);

        var decision = NotificationPolicyCalculator.Evaluate(
            NotificationChannel.Email,
            category: "Routine",
            NotificationPriority.Normal,
            new DateTime(2026, 1, 15, 4, 0, 0, DateTimeKind.Utc),
            policy,
            userPreference: null);

        Assert.True(decision.ShouldHold);
        Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), decision.NextAllowedAtUtc);
    }

    [Fact]
    public void Spring_forward_invalid_boundary_advances_to_first_valid_local_instant()
    {
        var policy = CreatePolicy(
            "America/New_York",
            startMinutes: 22 * 60,
            endMinutes: 2 * 60 + 30);

        var decision = NotificationPolicyCalculator.Evaluate(
            NotificationChannel.Email,
            category: "Routine",
            NotificationPriority.Normal,
            new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc),
            policy,
            userPreference: null);

        Assert.True(decision.ShouldHold);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), decision.NextAllowedAtUtc);
    }

    [Fact]
    public void Fall_back_ambiguous_boundary_uses_later_instant_to_avoid_early_release()
    {
        var policy = CreatePolicy(
            "America/New_York",
            startMinutes: 22 * 60,
            endMinutes: 1 * 60 + 30);

        var decision = NotificationPolicyCalculator.Evaluate(
            NotificationChannel.Email,
            category: "Routine",
            NotificationPriority.Normal,
            new DateTime(2026, 11, 1, 4, 30, 0, DateTimeKind.Utc),
            policy,
            userPreference: null);

        Assert.True(decision.ShouldHold);
        Assert.Equal(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), decision.NextAllowedAtUtc);
    }

    [Theory]
    [InlineData("Security.LoginRisk", NotificationPriority.Normal)]
    [InlineData("Urgent.Result", NotificationPriority.Normal)]
    [InlineData("Routine", NotificationPriority.High)]
    [InlineData("Routine", NotificationPriority.Critical)]
    public void Mandatory_bypass_categories_and_priorities_are_never_delayed(
        string category,
        NotificationPriority priority)
    {
        var policy = CreatePolicy("UTC", 0, 23 * 60 + 59);

        var decision = NotificationPolicyCalculator.Evaluate(
            NotificationChannel.Email,
            category,
            priority,
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            policy,
            userPreference: null);

        Assert.False(decision.ShouldHold);
        Assert.True(decision.Bypassed);
    }

    private static NotificationPolicyAppService CreateService(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp dbContext,
        Guid? tenantId,
        string userId)
    {
        return new NotificationPolicyAppService(
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            new ConfigurableDataFilter(enabled: true),
            new TestCurrentTenant(tenantId),
            new TestCurrentPrincipalAccessor(userId),
            new NotificationsGrantedPermissionChecker(
                NotificationsPermissions.Policy.Read,
                NotificationsPermissions.Policy.Manage,
                NotificationsPermissions.Inbox.Read),
            TimeProvider.System,
            SharedOnlyTenantDatabases.FanOut());
    }

    private static TenantNotificationPolicy CreatePolicy(string timeZoneId, int startMinutes, int endMinutes)
    {
        var policy = new TenantNotificationPolicy(Guid.NewGuid(), Guid.NewGuid());
        policy.Update(
            timeZoneId,
            true,
            startMinutes,
            endMinutes,
            NotificationPolicyChannel.Email,
            [],
            NotificationDigestFrequency.Disabled);
        return policy;
    }

    private static IEnumerable<PermissionDefinition> Flatten(IEnumerable<PermissionDefinition> permissions)
    {
        foreach (var permission in permissions)
        {
            yield return permission;
            foreach (var child in Flatten(permission.Children))
            {
                yield return child;
            }
        }
    }

    private static void AssertActionPolicy(string actionName, string policy, string route, bool audited)
    {
        var action = typeof(NotificationPolicyController).GetMethod(actionName);
        Assert.NotNull(action);
        Assert.Equal(policy, action!.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        var routeAttribute = Assert.Single(
            action.GetCustomAttributes().Where(attribute =>
                attribute is HttpGetAttribute or HttpPutAttribute or HttpPostAttribute));
        var actualRoute = routeAttribute switch
        {
            HttpGetAttribute get => get.Template,
            HttpPutAttribute put => put.Template,
            HttpPostAttribute post => post.Template,
            _ => null
        };
        Assert.Equal(route, actualRoute);
        Assert.Equal(audited, action.GetCustomAttribute<AuditedAttribute>() != null);
    }
}
