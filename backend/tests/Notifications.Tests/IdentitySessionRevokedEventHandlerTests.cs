using Notifications.Application.Contracts.Realtime;
using Notifications.Realtime;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

public sealed class IdentitySessionRevokedEventHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handler_should_publish_the_exact_revoked_session_with_explicit_scope(bool tenantScoped)
    {
        var publisher = new RecordingRealtimePublisher();
        var handler = new IdentitySessionRevokedEventHandler(publisher);
        var tenantId = tenantScoped ? Guid.NewGuid() : (Guid?)null;
        var userId = Guid.NewGuid();
        var occurredAtUtc = DateTime.UtcNow;

        if (tenantScoped)
        {
            await handler.HandleAsync(new IdentitySessionRevokedTenantEto
            {
                TenantId = tenantId,
                UserId = userId,
                SessionId = "session-123",
                Action = "Logout",
                OccurredAtUtc = occurredAtUtc
            });
        }
        else
        {
            await handler.HandleAsync(new IdentitySessionRevokedHostEto
            {
                UserId = userId,
                SessionId = "session-123",
                Action = "Logout",
                OccurredAtUtc = occurredAtUtc
            });
        }

        Assert.NotNull(publisher.SessionRevoked);
        Assert.Equal(tenantId, publisher.SessionRevoked.TenantId);
        Assert.Equal(userId.ToString("D"), publisher.SessionRevoked.UserId);
        Assert.Equal("session-123", publisher.SessionRevoked.SessionId);
        Assert.Equal("Logout", publisher.SessionRevoked.Action);
        Assert.Equal(occurredAtUtc, publisher.SessionRevoked.OccurredAtUtc);
    }

    private sealed class RecordingRealtimePublisher : INotificationsRealtimePublisher
    {
        public SessionRevokedSignalDto? SessionRevoked { get; private set; }

        public Task PublishAppNotificationAsync(
            AppNotificationSignalDto input,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishSessionRevokedAsync(
            SessionRevokedSignalDto input,
            CancellationToken cancellationToken = default)
        {
            SessionRevoked = input;
            return Task.CompletedTask;
        }
    }
}
