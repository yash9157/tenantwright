using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.UserNotifications;
using Notifications.Application.Delivery;
using Notifications.Application.Notifications;
using Notifications.Application.UserNotifications;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Shared.Permissions.Definitions.Notifications;
using Xunit;

namespace Notifications.Tests;

/// <summary>
/// Coverage for the rendered inbox content added for the App-channel inbox: the structured
/// safe-text formatter, the <see cref="UserNotification.SetRenderedContent"/> domain method, and
/// the read-side projection/fallback in <see cref="UserNotificationAppService.GetInboxAsync"/>.
/// </summary>
public class UserNotificationRenderedContentTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string UserId = "user-1";

    // ---- NotificationTextFormatter.ToStructuredText ----

    [Fact]
    public void ToStructuredText_preserves_block_boundaries_as_line_breaks_and_strips_markup()
    {
        const string html = "<p>Your request is <b>confirmed</b>.</p><p>Room 4B<br>2:30 PM</p>";

        var text = NotificationTextFormatter.ToStructuredText(html);

        // Paragraph boundary -> blank line; inline <b> stripped; <br> -> single line break.
        Assert.Equal("Your request is confirmed.\n\nRoom 4B\n2:30 PM", text);
        Assert.DoesNotContain('<', text);
        Assert.DoesNotContain('>', text);
    }

    [Fact]
    public void ToStructuredText_decodes_entities_and_collapses_intra_line_spaces()
    {
        const string html = "<p>Balance:&nbsp;&#36;100   &amp;   fees</p>";

        var text = NotificationTextFormatter.ToStructuredText(html);

        // &nbsp; decodes to a non-breaking space; assert the visible content and no markup remains.
        Assert.Contains("Balance:", text, StringComparison.Ordinal);
        Assert.Contains("$100 & fees", text, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("&#36;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToStructuredText_caps_consecutive_blank_lines_and_trims()
    {
        const string html = "<div>Line 1</div><br><br><br><div>Line 2</div>";

        var text = NotificationTextFormatter.ToStructuredText(html);

        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
        Assert.Equal("Line 1\n\nLine 2", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToStructuredText_returns_empty_for_blank_input(string? input)
    {
        Assert.Equal(string.Empty, NotificationTextFormatter.ToStructuredText(input));
    }

    // ---- UserNotification.SetRenderedContent ----

    [Fact]
    public void SetRenderedContent_trims_values_and_nulls_blanks()
    {
        var inbox = new UserNotification(1, Tenant, Guid.NewGuid(), UserId);

        inbox.SetRenderedContent("  Title  ", "   ", "  Body text  ");

        Assert.Equal("Title", inbox.Title);
        Assert.Null(inbox.Summary);
        Assert.Equal("Body text", inbox.Body);
    }

    [Fact]
    public void SetRenderedContent_accepts_all_null()
    {
        var inbox = new UserNotification(1, Tenant, Guid.NewGuid(), UserId);

        inbox.SetRenderedContent(null, null, null);

        Assert.Null(inbox.Title);
        Assert.Null(inbox.Summary);
        Assert.Null(inbox.Body);
    }

    // ---- GetInboxAsync projection ----

    [Fact]
    public async Task GetInbox_projects_rendered_title_summary_and_body()
    {
        using var db = BuildContext();
        var notification = NewNotification(subject: "Subject line");
        db.Notifications.Add(notification);

        var inbox = new UserNotification(1, Tenant, notification.Id, UserId);
        inbox.SetRenderedContent("Rendered title", "Rendered summary", "Rendered body\nsecond line");
        db.UserNotifications.Add(inbox);
        await db.SaveChangesAsync();

        var page = await BuildService(db).GetInboxAsync(new GetUserNotificationsInput { MaxResultCount = 100 });

        var item = Assert.Single(page.Items);
        Assert.Equal("Rendered title", item.Title);
        Assert.Equal("Rendered summary", item.Summary);
        Assert.Equal("Rendered body\nsecond line", item.Body);
    }

    [Fact]
    public async Task GetInbox_falls_back_to_subject_then_template_when_unrendered()
    {
        using var db = BuildContext();
        var withSubject = NewNotification(subject: "Has subject");
        var withoutSubject = NewNotification(subject: null, templateName: "Welcome.Tpl");
        db.Notifications.AddRange(withSubject, withoutSubject);

        // No SetRenderedContent call -> Title/Body/Summary remain null on the entity.
        db.UserNotifications.Add(new UserNotification(1, Tenant, withSubject.Id, UserId));
        db.UserNotifications.Add(new UserNotification(2, Tenant, withoutSubject.Id, UserId));
        await db.SaveChangesAsync();

        var page = await BuildService(db).GetInboxAsync(new GetUserNotificationsInput { MaxResultCount = 100 });

        var subjectItem = Assert.Single(page.Items, x => x.NotificationId == withSubject.Id);
        Assert.Equal("Has subject", subjectItem.Title);
        Assert.Null(subjectItem.Body);
        Assert.Null(subjectItem.Summary);

        var templateItem = Assert.Single(page.Items, x => x.NotificationId == withoutSubject.Id);
        Assert.Equal("Welcome.Tpl", templateItem.Title);
        Assert.Null(templateItem.Body);
    }

    private static Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp BuildContext()
    {
        return NotificationsEfCoreTestHarness.BuildDbContext(
            Guid.NewGuid().ToString(), tenantId: Tenant, multiTenantFilterEnabled: true);
    }

    private static UserNotificationAppService BuildService(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp db)
    {
        return new UserNotificationAppService(
            new EfTestRepository<UserNotification, long>(db),
            NotificationsEfCoreTestHarness.AsDbContextProvider(db),
            new TestCurrentTenant(Tenant),
            new TestCurrentPrincipalAccessor(UserId),
            Options.Create(new NotificationsQuerySecurityOptions()),
            new NotificationsGrantedPermissionChecker(NotificationsPermissions.Inbox.Read),
            TimeProvider.System);
    }

    private static Notification NewNotification(string? subject = "S", string templateName = "Tpl")
    {
        return new Notification(
            id: Guid.NewGuid(),
            tenantId: Tenant,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: templateName,
            templateModelJson: "{}",
            subject: subject,
            category: null,
            priority: NotificationPriority.Normal,
            actionUrl: null);
    }
}
