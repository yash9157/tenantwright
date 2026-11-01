using Shared.EventContracts;
using Microsoft.EntityFrameworkCore;
using Permissions.Application.Caching;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.RoleLifecycle;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<IdentityPlatformRoleRetirementRequestedEto>),
    IncludeSelf = true)]
public sealed class IdentityPlatformRoleRetirementRequestedEventHandler
    : ITenantWrightMessageHandler<IdentityPlatformRoleRetirementRequestedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;
    private readonly PermissionStampService _stampService;
    private readonly ITenantWrightMessageSender _messages;

    public IdentityPlatformRoleRetirementRequestedEventHandler(
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
        PermissionStampService stampService,
        ITenantWrightMessageSender messages)
    {
        _dbContextProvider = dbContextProvider;
        _stampService = stampService;
        _messages = messages;
    }

    public async Task HandleAsync(
        IdentityPlatformRoleRetirementRequestedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.RequestId == Guid.Empty || eventData.RoleId == Guid.Empty || eventData.LifecycleVersion <= 0)
        {
            return;
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var projection = await dbContext.IdentityPlatformRoleProjections
            .SingleOrDefaultAsync(x => x.Id == eventData.RoleId, cancellationToken);
        if (projection == null)
        {
            projection = new IdentityPlatformRoleProjection(eventData.RoleId, 0, eventData.OccurredAtUtc);
            dbContext.IdentityPlatformRoleProjections.Add(projection);
        }

        if (!projection.BeginRetirement(eventData.RequestId, eventData.LifecycleVersion, eventData.OccurredAtUtc))
        {
            return;
        }

        var providerKey = eventData.RoleId.ToString("D");
        var grants = await dbContext.PermissionGrants
            .Where(x => x.TenantId == null &&
                        x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);
        dbContext.PermissionGrants.RemoveRange(grants);

        var pendingRequests = await dbContext.PermissionGrantChangeRequests
            .Where(x => x.TenantId == null &&
                        x.ProviderName == PermissionProviderNames.Role &&
                        x.ProviderKey == providerKey &&
                        x.Status == PermissionGrantChangeRequestStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var request in pendingRequests)
        {
            request.Cancel(decidedByUserId: null, "Role retired", eventData.OccurredAtUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await _stampService.BumpAsync(
            null,
            PermissionProviderNames.Role,
            providerKey,
            cancellationToken: cancellationToken);
        await _messages.PublishEventAsync(
            new PermissionsPlatformRoleRetirementCompletedEto
            {
                RequestId = eventData.RequestId,
                RoleId = eventData.RoleId,
                LifecycleVersion = eventData.LifecycleVersion,
                OccurredAtUtc = DateTime.UtcNow
            },
            cancellationToken);
    }
}
