using Identity.Application.Account;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Users;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantClosureChangedEto>),
    IncludeSelf = true)]
public sealed class TenantClosureChangedEventHandler
    : ITenantWrightMessageHandler<TenantClosureChangedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ITenantWrightMessageSender _messages;

    public TenantClosureChangedEventHandler(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider,
        ITenantWrightMessageSender eventBus)
    {
        _dbContextProvider = dbContextProvider;
        _messages = eventBus;
    }

    public async Task HandleAsync(
        TenantClosureChangedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.OwnerUserId.HasValue || eventData.TenantId == Guid.Empty)
        {
            return;
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var email = await unitOfWorkDbContext.Users
            .Where(user => user.Id == eventData.OwnerUserId.Value && user.TenantId == eventData.TenantId)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        await _messages.SendCommandAsync(
            new NotificationSendRequestedTenantEto
            {
                RequestId = eventData.RequestId,
                TenantId = eventData.TenantId,
                OccurredAtUtc = eventData.OccurredAtUtc,
                SourceService = "tenant-closure",
                DeliveryPlan = new NotificationDeliveryPlan
                {
                    Mode = NotificationDeliveryMode.Parallel,
                    Steps = [new NotificationChannelPlan
                        {
                            Channel = NotificationSendChannel.Email,
                            Subject = eventData.IsClosed ? "Tenant closed" : "Tenant restored",
                            TemplateName = eventData.IsClosed ? "Tenant.Closure" : "Tenant.Restored",
                            TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["reasonCode"] = eventData.ReasonCode
                }
                        }]
                },
                Recipients = IdentityNotificationContractPublisher.CreateSingleChannelRecipients(NotificationSendChannel.Email, [
                    new NotificationRecipientEto
                    {
                        RecipientType = NotificationSendRecipientType.EmailAddress,
                        RecipientValue = email
                    }
                ], null),
            },
            cancellationToken: cancellationToken);
    }
}
