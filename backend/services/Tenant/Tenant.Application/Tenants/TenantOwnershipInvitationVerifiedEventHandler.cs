using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using Tenant.Domain.Shared.Enums;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipInvitationVerifiedEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipInvitationVerifiedEventHandler
    : ITenantWrightMessageHandler<TenantOwnershipInvitationVerifiedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly ITenantWrightMessageSender _messages;
    private readonly TenantCacheManager _tenantCacheManager;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<TenantOwnershipInvitationVerifiedEventHandler> _logger;

    public TenantOwnershipInvitationVerifiedEventHandler(
        IDbContextProvider<TenantDbContextApp> dbContextProvider,
        ITenantWrightMessageSender messages,
        TenantCacheManager tenantCacheManager,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<TenantOwnershipInvitationVerifiedEventHandler> logger)
    {
        _dbContextProvider = dbContextProvider;
        _messages = messages;
        _tenantCacheManager = tenantCacheManager;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    public async Task HandleAsync(
        TenantOwnershipInvitationVerifiedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.RequestId == Guid.Empty ||
            eventData.TenantId == Guid.Empty ||
            eventData.UserId == Guid.Empty)
        {
            return;
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var request = await unitOfWorkDbContext.TenantOwnershipRequests.FirstOrDefaultAsync(
            x => x.Id == eventData.RequestId && x.TenantId == eventData.TenantId,
            cancellationToken);
        if (request == null || request.Status == TenantOwnershipRequestStatus.Completed)
        {
            return;
        }

        var tenant = await unitOfWorkDbContext.Tenants
            .Include(x => x.ConnectionStrings)
            .FirstOrDefaultAsync(x => x.Id == eventData.TenantId, cancellationToken);
        if (tenant == null)
        {
            return;
        }

        var previousOwnerUserId = tenant.OwnerUserId;
        if (!request.Complete(eventData.UserId, eventData.OccurredAtUtc))
        {
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // Denormalize owner display: prefer the composed display name, then the user name.
        var ownerDisplayName = string.IsNullOrWhiteSpace(eventData.DisplayName)
            ? eventData.UserName
            : eventData.DisplayName;
        tenant.SetOwner(eventData.UserId, ownerDisplayName, eventData.Email);
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);

        if (request.Kind == TenantOwnershipRequestKind.InitialOwner)
        {
            await _messages.SendCommandAsync(
                new TenantInitialOwnerOnboardingCompletedEto
                {
                    RequestId = request.Id,
                    TenantId = tenant.Id,
                    OccurredAtUtc = eventData.OccurredAtUtc
                },
                cancellationToken: cancellationToken);
        }

        (_unitOfWorkManager.Current
                ?? throw new InvalidOperationException("Tenant ownership completion requires an active unit of work."))
            .OnCompleted(() => RefreshTenantCacheAfterCommitAsync(tenant));

        await _messages.PublishEventAsync(
            new TenantOwnershipCompletedEto
            {
                RequestId = request.Id,
                TenantId = tenant.Id,
                NewOwnerUserId = eventData.UserId,
                PreviousOwnerUserId = previousOwnerUserId,
                OccurredAtUtc = eventData.OccurredAtUtc
            },
            cancellationToken: cancellationToken);
    }

    private async Task RefreshTenantCacheAfterCommitAsync(global::Tenant.Domain.Tenants.Tenant tenant)
    {
        try
        {
            await _tenantCacheManager.UpsertTenantAsync(tenant, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // TenantCacheReconcileWorker is the durable recovery path for post-commit cache drift.
            _logger.LogWarning(
                exception,
                "Failed to refresh tenant cache after ownership completion for tenant {TenantId}.",
                tenant.Id);
        }
    }
}
