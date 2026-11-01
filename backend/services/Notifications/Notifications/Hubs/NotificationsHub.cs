using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Presence;
using Notifications.Application.Contracts.Realtime;
using Shared.Security;

namespace Notifications.Hubs;

[Authorize]
public sealed class NotificationsHub : Hub
{
    private readonly INotificationsPresenceStore _presenceStore;
    private readonly IOptions<NotificationsPresenceOptions> _presenceOptions;

    public NotificationsHub(INotificationsPresenceStore presenceStore, IOptions<NotificationsPresenceOptions> presenceOptions)
    {
        _presenceStore = presenceStore;
        _presenceOptions = presenceOptions;
    }

    public override async Task OnConnectedAsync()
    {
        var (tenantId, userId) = GetIdentity();
        var group = NotificationsRealtimeGroupNames.UserGroup(tenantId, userId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        if (_presenceOptions.Value.Enabled)
        {
            await _presenceStore.SetActiveAsync(tenantId, userId, Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }

    public Task Heartbeat()
    {
        var (tenantId, userId) = GetIdentity();
        return _presenceStore.SetActiveAsync(tenantId, userId, Context.ConnectionAborted);
    }

    private (Guid? TenantId, string UserId) GetIdentity()
    {
        var principal = Context.User;
        var userId = principal?.FindFirstValue(SharedClaimTypes.Subject) ?? principal?.FindFirstValue(SharedClaimTypes.UserId);
        userId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new HubException("Missing user identity.");
        }

        Guid? tenantId = null;
        var tenantIdClaim = principal?.FindFirstValue(SharedClaimTypes.TenantId);
        if (Guid.TryParse(tenantIdClaim, out var parsedTenantId))
        {
            tenantId = parsedTenantId;
        }

        return (tenantId, userId);
    }
}
