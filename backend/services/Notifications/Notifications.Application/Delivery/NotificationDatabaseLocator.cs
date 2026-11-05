using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Shared.Constants;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;

namespace Notifications.Application.Delivery;

/// <summary>
/// Host-scope work that addresses one notification row without knowing its tenant — provider webhooks (delivery
/// receipts, bounces, status callbacks) and host operator lookups by id — must run in the database that owns the row:
/// a row of a tenant with a dedicated Notifications database lives only there. This locator probes every database
/// through <see cref="TenantDatabaseFanOut"/> and enters the owner's scope: the owning tenant's scope for a dedicated
/// database, the unchanged host scope for the shared database or an unknown key (which then reads as not found).
/// Lookups return the owning database; callers enter it with <see cref="Enter"/> in the method that runs the work,
/// because the ambient tenant is async-local and a scope entered inside an async helper would not reach the caller.
/// Correlation, authorization and writes run unchanged inside that scope, so they can never touch another tenant's
/// database, and an unreachable database fails the lookup instead of silently resolving to the shared one.
/// </summary>
public sealed class NotificationDatabaseLocator : ITransientDependency
{
    private const string Work = "notifications-owning-database-lookup";

    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;

    public NotificationDatabaseLocator(
        TenantDatabaseFanOut fanOut,
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor)
    {
        _fanOut = fanOut;
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
    }

    /// <summary>Enters <paramref name="database"/> (see <see cref="TenantDatabaseFanOut.EnterDatabase"/>).</summary>
    public IDisposable Enter(TenantDatabase? database) => _fanOut.EnterDatabase(database);

    /// <summary>The database owning the delivery with <paramref name="providerMessageId"/>; null for the shared one.</summary>
    public async Task<TenantDatabase?> FindProviderMessageDatabaseAsync(
        string? providerMessageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
            return null;
        }

        return await FindFirstOwnerAsync(
            (scope, token) => ProbeAsync(
                (dbContext, operationToken) => dbContext.NotificationDeliveries
                    .IgnoreQueryFilters()
                    .WhereOwnedBy(scope)
                    .AnyAsync(x => x.ProviderMessageId == providerMessageId, operationToken),
                token),
            cancellationToken);
    }

    /// <summary>
    /// The database that stores provider configuration <paramref name="providerConfigId"/>, for callbacks routed by
    /// configuration id (for example Meta WhatsApp webhooks).
    /// </summary>
    public Task<TenantDatabase?> FindProviderConfigDatabaseAsync(
        Guid providerConfigId,
        CancellationToken cancellationToken = default) =>
        FindFirstOwnerAsync(
            (scope, token) => ProbeAsync(
                (dbContext, operationToken) => dbContext.NotificationProviderConfigs
                    .IgnoreQueryFilters()
                    .WhereOwnedBy(scope)
                    .AnyAsync(x => x.Id == providerConfigId, operationToken),
                token),
            cancellationToken);

    /// <summary>The database that stores notification <paramref name="notificationId"/> (a Guid key).</summary>
    public Task<TenantDatabase?> FindNotificationDatabaseAsync(
        Guid notificationId,
        CancellationToken cancellationToken = default) =>
        FindFirstOwnerAsync(
            (scope, token) => ProbeAsync(
                (dbContext, operationToken) => dbContext.Notifications
                    .IgnoreQueryFilters()
                    .WhereOwnedBy(scope)
                    .AnyAsync(x => x.Id == notificationId, operationToken),
                token),
            cancellationToken);

    /// <summary>
    /// The database that stores delivery <paramref name="deliveryId"/>. Delivery ids are identity values, unique per
    /// database only: with <paramref name="tenantId"/> the tenant's own database is used directly; without it a key
    /// present in more than one database is rejected so the caller names the tenant instead of reading another
    /// tenant's row with the same id.
    /// </summary>
    public async Task<TenantDatabase?> FindDeliveryDatabaseAsync(
        long deliveryId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId.HasValue)
        {
            return await _fanOut.FindTenantDatabaseAsync<NotificationsDbContextApp>(tenantId.Value, cancellationToken);
        }

        var owners = await _fanOut.FindOwningDatabasesAsync<NotificationsDbContextApp>(
            Work,
            (scope, token) => ProbeAsync(
                (dbContext, operationToken) => dbContext.NotificationDeliveries
                    .IgnoreQueryFilters()
                    .WhereOwnedBy(scope)
                    .AnyAsync(x => x.Id == deliveryId, operationToken),
                token),
            cancellationToken);
        if (owners.Count > 1)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.DeliveryTenantRequired,
                "The delivery id exists in more than one tenant database; specify the tenant.");
        }

        return owners.Count == 0 ? null : owners[0];
    }

    private async Task<TenantDatabase?> FindFirstOwnerAsync(
        Func<TenantDatabaseScope, CancellationToken, Task<bool>> owns,
        CancellationToken cancellationToken)
    {
        var owners = await _fanOut.FindOwningDatabasesAsync<NotificationsDbContextApp>(Work, owns, cancellationToken);
        if (owners.Count > 1)
            throw new SharedConflictException(NotificationErrorCodes.DatabaseOwnershipAmbiguous,
                "More than one database owns the notification correlation identity.");
        return owners.Count == 0 ? null : owners[0];
    }

    public Task<TenantDatabase?> FindDispatchDatabaseAsync(Guid dispatchId, CancellationToken cancellationToken = default) =>
        FindFirstOwnerAsync((scope, token) => ProbeAsync(
            (context, operationToken) => context.NotificationDispatches.IgnoreQueryFilters().WhereOwnedBy(scope)
                .AnyAsync(dispatch => dispatch.Id == dispatchId && !dispatch.IsDeleted, operationToken), token), cancellationToken);

    private Task<bool> ProbeAsync(
        Func<NotificationsDbContextApp, CancellationToken, Task<bool>> probe,
        CancellationToken cancellationToken) =>
        _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            async operationToken => await probe(await _dbContextProvider.GetDbContextAsync(), operationToken),
            cancellationToken);
}
