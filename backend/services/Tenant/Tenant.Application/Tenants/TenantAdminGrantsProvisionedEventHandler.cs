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
    typeof(ITenantWrightMessageHandler<TenantAdminGrantsProvisionedEto>),
    IncludeSelf = true)]
public sealed class TenantAdminGrantsProvisionedEventHandler
    : ITenantWrightMessageHandler<TenantAdminGrantsProvisionedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly ITenantWrightMessageSender _messages;
    private readonly TenantCacheManager _tenantCacheManager;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<TenantAdminGrantsProvisionedEventHandler> _logger;

    public TenantAdminGrantsProvisionedEventHandler(
        IDbContextProvider<TenantDbContextApp> dbContextProvider,
        ITenantWrightMessageSender messages,
        TenantCacheManager tenantCacheManager,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<TenantAdminGrantsProvisionedEventHandler> logger)
    {
        _dbContextProvider = dbContextProvider;
        _messages = messages;
        _tenantCacheManager = tenantCacheManager;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    public async Task HandleAsync(
        TenantAdminGrantsProvisionedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.RequestId == Guid.Empty ||
            eventData.TenantId == Guid.Empty ||
            eventData.OwnerUserId == Guid.Empty)
        {
            return;
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var request = await unitOfWorkDbContext.TenantOwnershipRequests
            .FirstOrDefaultAsync(
                x => x.Id == eventData.RequestId && x.TenantId == eventData.TenantId,
                cancellationToken);
        var tenant = await unitOfWorkDbContext.Tenants
            .Include(x => x.ConnectionStrings)
            .FirstOrDefaultAsync(x => x.Id == eventData.TenantId, cancellationToken);
        if (request == null ||
            tenant == null ||
            request.Kind != TenantOwnershipRequestKind.InitialOwner ||
            request.TargetUserId != eventData.OwnerUserId ||
            tenant.OwnerUserId != eventData.OwnerUserId)
        {
            return;
        }

        if (!request.AcknowledgePermissionsProvisioned(eventData.OwnerUserId, eventData.OccurredAtUtc) ||
            tenant.GetEffectiveLifecycleStatus() != TenantLifecycleStatus.Provisioning)
        {
            return;
        }

        tenant.TransitionTo(TenantLifecycleStatus.Active);
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        (_unitOfWorkManager.Current
                ?? throw new InvalidOperationException("Tenant activation requires an active unit of work."))
            .OnCompleted(() => RefreshTenantCacheAfterCommitAsync(tenant));

        await _messages.PublishEventAsync(
            new TenantLifecycleChangedEto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                LifecycleRevision = tenant.LifecycleRevision,
                PreviousStatus = TenantLifecycleStatusEto.Provisioning,
                CurrentStatus = TenantLifecycleStatusEto.Active,
                ReasonCode = "tenant-admin-permissions-ready",
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
                "Failed to refresh tenant cache after tenant activation for tenant {TenantId}.",
                tenant.Id);
        }
    }
}
