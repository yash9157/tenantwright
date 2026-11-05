using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Notifications;
using Notifications.Application.Contracts.UserNotifications;
using Notifications.Application.Notifications;
using Notifications.Application.UserNotifications;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.AuditLogs.Publishing;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Notifications;
using System.Security.Claims;
using Xunit;

namespace Notifications.Tests;

public class NotificationQueryNavigationTests
{
    [Fact]
    public async Task Delivery_query_uses_navigation_to_read_recipient_fields()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(nameof(Delivery_query_uses_navigation_to_read_recipient_fields), tenantId);

        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: "T",
            templateModelJson: "{}");

        var recipient = new NotificationRecipient(
            id: 1,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.UserId,
            recipientValue: "u1");

        var delivery = new NotificationDelivery(
            id: 1,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientId: recipient.Id);
        delivery.MarkProviderAccepted(DateTime.UtcNow, providerMessageId: null);

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(recipient);
        dbContext.NotificationDeliveries.Add(delivery);
        await dbContext.SaveChangesAsync();

        var service = CreateQueryService(
            dbContext,
            tenantId,
            securityOptions: new NotificationsQuerySecurityOptions
            {
                IncludeRecipientValue = true,
                IncludeLastError = true
            });

        var deliveries = await service.GetDeliveriesAsync(notification.Id);
        Assert.Single(deliveries);
        Assert.Equal(NotificationRecipientType.UserId, deliveries[0].RecipientType);
        Assert.Equal("u1", deliveries[0].RecipientValue);
    }

    [Fact]
    public async Task Inbox_query_uses_navigation_to_read_notification_fields()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(nameof(Inbox_query_uses_navigation_to_read_notification_fields), tenantId);

        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: "Tenant.TenantCreated",
            templateModelJson: "{\"x\":\"1\"}",
            subject: "S");

        var inbox = new UserNotification(
            id: 1,
            tenantId: tenantId,
            notificationId: notification.Id,
            userId: "u1");

        dbContext.Notifications.Add(notification);
        dbContext.UserNotifications.Add(inbox);
        await dbContext.SaveChangesAsync();

        var service = new UserNotificationAppService(
            new EfTestRepository<UserNotification, long>(dbContext),
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            new TestCurrentTenant(tenantId),
            new TestCurrentPrincipalAccessor("u1"),
            Options.Create(new NotificationsQuerySecurityOptions
            {
                IncludeRecipientValue = true,
                IncludeLastError = true
            }),
            new NotificationsGrantedPermissionChecker(NotificationsPermissions.Inbox.Read),
            TimeProvider.System);

        var page = await service.GetInboxAsync(new GetUserNotificationsInput());
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal("Tenant.TenantCreated", page.Items[0].TemplateName);
        Assert.Equal(string.Empty, page.Items[0].TemplateModelJson);
        Assert.Equal("S", page.Items[0].Subject);
    }

    [Fact]
    public async Task Inbox_query_redacts_template_model_by_default()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(nameof(Inbox_query_redacts_template_model_by_default), tenantId);

        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: "Tenant.TenantCreated",
            templateModelJson: "{\"secret\":\"value\"}",
            subject: "S");

        var inbox = new UserNotification(
            id: 1,
            tenantId: tenantId,
            notificationId: notification.Id,
            userId: "u1");

        dbContext.Notifications.Add(notification);
        dbContext.UserNotifications.Add(inbox);
        await dbContext.SaveChangesAsync();

        var service = new UserNotificationAppService(
            new EfTestRepository<UserNotification, long>(dbContext),
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            new TestCurrentTenant(tenantId),
            new TestCurrentPrincipalAccessor("u1"),
            Options.Create(new NotificationsQuerySecurityOptions()),
            new NotificationsGrantedPermissionChecker(NotificationsPermissions.Inbox.Read),
            TimeProvider.System);

        var page = await service.GetInboxAsync(new GetUserNotificationsInput());
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(string.Empty, page.Items[0].TemplateModelJson);
    }

    [Fact]
    public async Task Notification_history_query_returns_paged_delivery_summary_with_filters()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(nameof(Notification_history_query_returns_paged_delivery_summary_with_filters), tenantId);
        var utcNow = DateTime.UtcNow;

        var sentNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: "{}",
            subject: "Reset password");
        sentNotification.CreationTime = utcNow.AddMinutes(-10);

        var queuedNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: "Tenant.Created",
            templateModelJson: "{}",
            subject: "Tenant created");
        queuedNotification.CreationTime = utcNow.AddMinutes(-5);

        var recipient = new NotificationRecipient(
            id: 1,
            tenantId: tenantId,
            notificationId: sentNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "admin@example.com");
        var sentDelivery = new NotificationDelivery(
            id: 1,
            tenantId: tenantId,
            notificationId: sentNotification.Id,
            recipientId: recipient.Id);
        sentDelivery.MarkProviderAccepted(utcNow, providerMessageId: "provider-1");

        var queuedRecipient = new NotificationRecipient(
            id: 2,
            tenantId: tenantId,
            notificationId: queuedNotification.Id,
            recipientType: NotificationRecipientType.UserId,
            recipientValue: "u1");
        var queuedDelivery = new NotificationDelivery(
            id: 2,
            tenantId: tenantId,
            notificationId: queuedNotification.Id,
            recipientId: queuedRecipient.Id);

        dbContext.Notifications.AddRange(sentNotification, queuedNotification);
        dbContext.NotificationRecipients.AddRange(recipient, queuedRecipient);
        dbContext.NotificationDeliveries.AddRange(sentDelivery, queuedDelivery);
        await dbContext.SaveChangesAsync();

        var service = CreateQueryService(dbContext, tenantId);

        var page = await service.GetListAsync(new GetNotificationsInput
        {
            TenantId = tenantId,
            Channel = NotificationChannel.Email,
            Status = NotificationStatus.ProviderAccepted,
            TemplateName = "Password",
            SkipCount = 0,
            MaxResultCount = 10
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(sentNotification.Id, item.Id);
        Assert.Equal(NotificationChannel.Email, item.Channel);
        Assert.Equal("Identity.PasswordReset", item.TemplateName);
        Assert.Equal(1, item.DeliveryCount);
        Assert.Equal(1, item.ProviderAcceptedCount);
        Assert.Equal(0, item.DeliveredCount);
        Assert.Equal(0, item.FailedCount);
    }

    [Fact]
    public async Task Current_tenant_recovery_groups_safe_owner_failures_without_recipient_payload()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Current_tenant_recovery_groups_safe_owner_failures_without_recipient_payload),
            tenantId);
        var notification = new Notification(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Email,
            "AuditLogs.ExportFailed", "{}", category: "audit", actionUrl: "/audit-logs/exports",
            sourceService: "AuditLogs");
        var recipient = new NotificationRecipient(
            9001, tenantId, notification.Id, NotificationRecipientType.EmailAddress,
            "private@example.test");
        var delivery = new NotificationDelivery(9002, tenantId, notification.Id, recipient.Id);
        delivery.MarkProcessing(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-4));
        delivery.MarkNeedsReview("provider_outcome_ambiguous", DateTime.UtcNow.AddMinutes(-3));
        dbContext.AddRange(notification, recipient, delivery);
        await dbContext.SaveChangesAsync();

        var rows = await CreateQueryService(dbContext, tenantId).GetCurrentTenantRecoveryAsync();

        var row = Assert.Single(rows);
        Assert.Equal("auditlogs", row.SourceService);
        Assert.Equal("audit", row.Category);
        Assert.Equal("/audit-logs/exports", row.ActionUrl);
        Assert.Equal(NotificationStatus.NeedsReview, row.Status);
        Assert.Equal(1, row.Count);
        Assert.False(row.CanRetry);
        Assert.DoesNotContain("private@example.test", row.SafeReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_application_boundary_denies_missing_exact_read_permission()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Query_application_boundary_denies_missing_exact_read_permission),
            tenantId);

        await Assert.ThrowsAsync<Shared.SharedForbiddenException>(() =>
            CreateQueryService(dbContext, tenantId, canRead: false)
                .GetCurrentTenantRecoveryAsync());
    }

    [Fact]
    public async Task Current_tenant_history_query_forces_current_tenant_and_blocks_cross_tenant_detail()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var databaseName = nameof(Current_tenant_history_query_forces_current_tenant_and_blocks_cross_tenant_detail);
        var databaseRoot = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName,
            tenantId,
            databaseRoot: databaseRoot);

        var currentTenantNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Current",
            templateModelJson: "{}",
            subject: "Current tenant");

        var otherTenantNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: otherTenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Other",
            templateModelJson: "{}",
            subject: "Other tenant");

        dbContext.Notifications.Add(currentTenantNotification);
        await dbContext.SaveChangesAsync();

        await using (var otherTenantDbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName,
            otherTenantId,
            databaseRoot: databaseRoot))
        {
            otherTenantDbContext.Notifications.Add(otherTenantNotification);
            await otherTenantDbContext.SaveChangesAsync();
        }

        var service = CreateQueryService(dbContext, tenantId);

        var page = await service.GetCurrentTenantListAsync(new GetNotificationsInput
        {
            TenantId = otherTenantId,
            SkipCount = 0,
            MaxResultCount = 10
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(currentTenantNotification.Id, item.Id);
        Assert.Equal(tenantId, item.TenantId);

        var currentDetail = await service.GetCurrentTenantAsync(currentTenantNotification.Id);
        Assert.Equal(currentTenantNotification.Id, currentDetail.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetCurrentTenantAsync(otherTenantNotification.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetCurrentTenantDeliveriesAsync(otherTenantNotification.Id));
    }

    [Fact]
    public async Task Notification_detail_redacts_without_sensitive_permission_and_audits_disclosure()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Notification_detail_redacts_without_sensitive_permission_and_audits_disclosure),
            tenantId);
        var notification = new Notification(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            NotificationChannel.Email,
            "Identity.PasswordReset",
            "{\"resetToken\":\"never-log-this\"}",
            "Reset password");
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync();

        var deniedAuditRequests = new List<AuditLogWriteRequest>();
        var deniedService = CreateQueryService(
            dbContext,
            tenantId,
            canReadSensitivePayload: false,
            auditRequests: deniedAuditRequests);
        var redacted = await deniedService.GetCurrentTenantAsync(notification.Id);

        Assert.Equal(string.Empty, redacted.TemplateModelJson);
        Assert.True(redacted.Redaction.RedactionApplied);
        Assert.Equal(1, redacted.Redaction.WithheldFieldCount);
        Assert.Equal([nameof(NotificationDto.TemplateModelJson)], redacted.Redaction.WithheldFields);
        Assert.Empty(deniedAuditRequests);

        var grantedAuditRequests = new List<AuditLogWriteRequest>();
        var grantedService = CreateQueryService(
            dbContext,
            tenantId,
            canReadSensitivePayload: true,
            auditRequests: grantedAuditRequests);
        var disclosed = await grantedService.GetCurrentTenantAsync(notification.Id);

        Assert.Equal("{\"resetToken\":\"never-log-this\"}", disclosed.TemplateModelJson);
        Assert.False(disclosed.Redaction.RedactionApplied);
        Assert.Equal(0, disclosed.Redaction.WithheldFieldCount);
        var audit = Assert.Single(grantedAuditRequests);
        Assert.Equal("notifications.sensitive-payload.read", audit.Category);
        Assert.Equal("operator-1", audit.UserId);
        Assert.DoesNotContain("never-log-this", string.Join('|', audit.Actions.SelectMany(action => action.Parameters.Values)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delivery_chronology_is_stably_paged_and_current_tenant_scoped()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var databaseName = nameof(Delivery_chronology_is_stably_paged_and_current_tenant_scoped);
        var databaseRoot = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName,
            tenantId,
            databaseRoot: databaseRoot);
        var now = DateTime.UtcNow;

        var notification = new Notification(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Sms, "Tenant.Alert", "{}");
        var recipient = new NotificationRecipient(
            7001, tenantId, notification.Id, NotificationRecipientType.PhoneNumber, "+910000000000");
        var delivery = new NotificationDelivery(7001, tenantId, notification.Id, recipient.Id);
        delivery.MarkProcessing(now.AddSeconds(1), now.AddMinutes(1));
        delivery.MarkFailed("provider_timeout", now.AddMinutes(5), now.AddSeconds(2), "timeout");

        var otherNotification = new Notification(
            Guid.NewGuid(), otherTenantId, Guid.NewGuid(), NotificationChannel.Sms, "Tenant.Other", "{}");
        var otherRecipient = new NotificationRecipient(
            7002, otherTenantId, otherNotification.Id, NotificationRecipientType.PhoneNumber, "+910000000001");
        var otherDelivery = new NotificationDelivery(7002, otherTenantId, otherNotification.Id, otherRecipient.Id);

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(recipient);
        dbContext.NotificationDeliveries.Add(delivery);
        await dbContext.SaveChangesAsync();

        await using (var otherTenantDbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName,
            otherTenantId,
            databaseRoot: databaseRoot))
        {
            otherTenantDbContext.Notifications.Add(otherNotification);
            otherTenantDbContext.NotificationRecipients.Add(otherRecipient);
            otherTenantDbContext.NotificationDeliveries.Add(otherDelivery);
            await otherTenantDbContext.SaveChangesAsync();
        }

        var service = CreateQueryService(dbContext, tenantId);
        var page = await service.GetCurrentTenantDeliveryChronologyAsync(
            delivery.Id,
            new GetNotificationDeliveryChronologyInput { SkipCount = 0, MaxResultCount = 2 });

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(NotificationDeliveryEventKind.Queued, page.Items[0].EventKind);
        Assert.Equal(NotificationDeliveryEventKind.Processing, page.Items[1].EventKind);
        Assert.True(page.Items[0].OccurredAtUtc <= page.Items[1].OccurredAtUtc);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetCurrentTenantDeliveryChronologyAsync(
                otherDelivery.Id,
                new GetNotificationDeliveryChronologyInput()));
    }

    private static NotificationQueryAppService CreateQueryService(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp dbContext,
        Guid? tenantId,
        bool canReadSensitivePayload = false,
        bool canRead = true,
        NotificationsQuerySecurityOptions? securityOptions = null,
        List<AuditLogWriteRequest>? auditRequests = null)
    {
        return new NotificationQueryAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new EfTestRepository<NotificationDeliveryChronology, long>(dbContext),
            Options.Create(securityOptions ?? new NotificationsQuerySecurityOptions()),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(tenantId),
            new TestPermissionChecker(canReadSensitivePayload, canRead),
            new TestCurrentPrincipalAccessor("operator-1"),
            new CapturingAuditPublisher(auditRequests ?? []),
            SharedOnlyTenantDatabases.FanOut(),
            SharedOnlyTenantDatabases.Locator());
    }

    [Theory]
    [InlineData(true, NotificationChannel.Email)]
    [InlineData(false, NotificationChannel.Push)]
    public async Task Routed_or_push_destinations_require_sensitive_permission_even_when_recipient_display_is_configured(bool routed, NotificationChannel channel)
    {
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId);
        var notification = new Notification(Guid.NewGuid(), tenantId, Guid.NewGuid(), channel, "test", "{}");
        if (routed)
            notification.AttachDispatchStep(Guid.NewGuid(), tenantId);
        var recipient = new NotificationRecipient(1, tenantId, notification.Id,
            channel == NotificationChannel.Email ? NotificationRecipientType.EmailAddress : NotificationRecipientType.DeviceToken, "sensitive-destination");
        context.Notifications.Add(notification);
        context.NotificationRecipients.Add(recipient);
        context.NotificationDeliveries.Add(new NotificationDelivery(1, tenantId, notification.Id, recipient.Id));
        await context.SaveChangesAsync();
        var options = new NotificationsQuerySecurityOptions { IncludeRecipientValue = true };
        var redacted = await CreateQueryService(context, tenantId, securityOptions: options).GetDeliveriesAsync(notification.Id);
        Assert.Empty(Assert.Single(redacted).RecipientValue);
        var audit = new List<AuditLogWriteRequest>();
        var disclosed = await CreateQueryService(context, tenantId, canReadSensitivePayload: true, securityOptions: options, auditRequests: audit)
            .GetDeliveriesAsync(notification.Id);
        Assert.Equal("sensitive-destination", Assert.Single(disclosed).RecipientValue);
        Assert.Single(audit);
    }

    private sealed class TestPermissionChecker(bool canReadSensitivePayload, bool canRead) : IPermissionChecker
    {
        public Task<bool> IsGrantedAsync(
            ClaimsPrincipal principal,
            string permissionName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                (canRead && permissionName == NotificationsPermissions.Query.Read) ||
                (canReadSensitivePayload &&
                 permissionName == NotificationsPermissions.Query.ReadSensitivePayload));
        }

        public Task<Dictionary<string, bool>> IsGrantedAsync(
            ClaimsPrincipal principal,
            IReadOnlyList<string> permissionNames,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(permissionNames.ToDictionary(
                permissionName => permissionName,
                permissionName => (canRead && permissionName == NotificationsPermissions.Query.Read) ||
                    (canReadSensitivePayload &&
                     permissionName == NotificationsPermissions.Query.ReadSensitivePayload),
                StringComparer.Ordinal));
        }
    }

    private sealed class CapturingAuditPublisher(List<AuditLogWriteRequest> requests)
        : IAuditLogWriteRequestPublisher
    {
        public Task PublishAsync(
            AuditLogWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
