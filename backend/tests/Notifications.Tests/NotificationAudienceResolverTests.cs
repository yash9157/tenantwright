using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

public class NotificationAudienceResolverTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public async Task No_group_recipients_returns_input_unchanged()
    {
        var resolver = Build(new FakeAudienceClient());
        var input = new List<NotificationRecipientEto>
        {
            new() { RecipientType = NotificationSendRecipientType.UserId, RecipientValue = Guid.NewGuid().ToString("D") }
        };

        var result = await resolver.ResolveAsync(Tenant, NotificationSendChannel.App, input);

        Assert.Same(input, result);
    }

    [Fact]
    public async Task Role_expands_to_user_recipients()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var resolver = Build(new FakeAudienceClient { ByRole = { ["Reviewer"] = [u1, u2] } });

        var result = await resolver.ResolveAsync(Tenant, NotificationSendChannel.App,
            [new() { RecipientType = NotificationSendRecipientType.Role, RecipientValue = "Reviewer" }]);

        Assert.All(result, r => Assert.Equal(NotificationSendRecipientType.UserId, r.RecipientType));
        Assert.Equal(new[] { u1, u2 }.OrderBy(x => x), result.Select(r => Guid.Parse(r.RecipientValue)).OrderBy(x => x));
    }

    [Fact]
    public async Task AllTenantUsers_expands_and_dedupes_with_literal_user()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var resolver = Build(new FakeAudienceClient { TenantUsers = [u1, u2] });

        var result = await resolver.ResolveAsync(Tenant, NotificationSendChannel.App,
        [
            new() { RecipientType = NotificationSendRecipientType.AllTenantUsers, RecipientValue = string.Empty },
            new() { RecipientType = NotificationSendRecipientType.UserId, RecipientValue = u1.ToString("D") } // duplicate of u1
        ]);

        Assert.Equal(2, result.Count); // u1 deduped
    }

    [Fact]
    public async Task Group_send_on_non_app_channel_is_rejected()
    {
        var resolver = Build(new FakeAudienceClient());
        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() => resolver.ResolveAsync(
            Tenant, NotificationSendChannel.Email,
            [new() { RecipientType = NotificationSendRecipientType.Role, RecipientValue = "Reviewer" }]));

        Assert.Equal("notification_group_channel_invalid", exception.Code);
    }

    [Fact]
    public async Task Group_send_without_tenant_is_rejected()
    {
        var resolver = Build(new FakeAudienceClient { ByRole = { ["Reviewer"] = [Guid.NewGuid()] } });
        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() => resolver.ResolveAsync(
            tenantId: null, NotificationSendChannel.App,
            [new() { RecipientType = NotificationSendRecipientType.Role, RecipientValue = "Reviewer" }]));

        Assert.Equal("notification_audience_tenant_required", exception.Code);
    }

    [Fact]
    public async Task Over_cap_audience_fails_closed()
    {
        var users = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var resolver = Build(new FakeAudienceClient { TenantUsers = users }, max: 3);

        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() => resolver.ResolveAsync(
            Tenant, NotificationSendChannel.App,
            [new() { RecipientType = NotificationSendRecipientType.AllTenantUsers, RecipientValue = string.Empty }]));

        Assert.Equal("notification_audience_limit_exceeded", exception.Code);
    }

    [Fact]
    public async Task Disabled_feature_rejects_group_send_fail_closed()
    {
        var resolver = new NotificationAudienceResolver(
            new FakeAudienceClient { ByRole = { ["Reviewer"] = [Guid.NewGuid()] } },
            Options.Create(new NotificationAudienceOptions { Enabled = false, MaxResolvedRecipients = 5000 }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            Tenant, NotificationSendChannel.App,
            [new() { RecipientType = NotificationSendRecipientType.Role, RecipientValue = "Reviewer" }]));
    }

    [Fact]
    public async Task Disabled_feature_still_passes_through_non_group_recipients()
    {
        var resolver = new NotificationAudienceResolver(
            new FakeAudienceClient(),
            Options.Create(new NotificationAudienceOptions { Enabled = false }));
        var input = new List<NotificationRecipientEto>
        {
            new() { RecipientType = NotificationSendRecipientType.UserId, RecipientValue = Guid.NewGuid().ToString("D") }
        };

        var result = await resolver.ResolveAsync(Tenant, NotificationSendChannel.App, input);
        Assert.Same(input, result);
    }

    private static NotificationAudienceResolver Build(IIdentityAudienceClient client, int max = 5000)
    {
        return new NotificationAudienceResolver(
            client,
            Options.Create(new NotificationAudienceOptions { Enabled = true, MaxResolvedRecipients = max }));
    }

    private sealed class FakeAudienceClient : IIdentityAudienceClient
    {
        public Dictionary<string, List<Guid>> ByRole { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Guid> TenantUsers { get; set; } = [];

        public Task<IReadOnlyList<Guid>> GetUsersByRoleAsync(Guid tenantId, string roleName, int maxUsers, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>(ByRole.TryGetValue(roleName, out var list) ? list : []);

        public Task<IReadOnlyList<Guid>> GetTenantUserIdsAsync(Guid tenantId, int maxUsers, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>(TenantUsers);
    }
}
