using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.AuditLogs.Publishing;

public sealed class AuditLogWriteRequestPublisher : IAuditLogWriteRequestPublisher, ITransientDependency
{
    private readonly ITenantWrightMessageSender _messageSender;
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly AuditLogEventSigner _signer;
    private readonly ICurrentTenant _currentTenant;

    public AuditLogWriteRequestPublisher(
        ITenantWrightMessageSender messageSender,
        IOptions<SharedAuditingOptions> options,
        AuditLogEventSigner signer,
        ICurrentTenant currentTenant)
    {
        _messageSender = messageSender;
        _options = options;
        _signer = signer;
        _currentTenant = currentTenant;
    }

    public Task PublishAsync(
        AuditLogWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = AuditLogWriteRequestValidation.Normalize(
            request,
            AuditSourceService.Resolve(_options.Value));
        _signer.TrySign(normalized);

        if (normalized.TenantId.HasValue && normalized.TenantId.Value != Guid.Empty)
        {
            return SendTenantCommandAsync(normalized, normalized.TenantId.Value, cancellationToken);
        }

        return _messageSender.SendCommandAsync(
            AuditLogContractMapper.ToHost(normalized), cancellationToken);
    }

    /// <summary>
    /// Tenant audit commands are stamped under the record's tenant. Host-context producers (for example the token
    /// endpoint auditing a failed tenant login) have no ambient tenant, so the scope is entered for them; an ambient
    /// tenant that differs from the record is left in place and outbound stamping rejects the cross-tenant command.
    /// </summary>
    private async Task SendTenantCommandAsync(
        AuditLogWriteRequest normalized,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        using (_currentTenant.Id.HasValue ? null : _currentTenant.Change(tenantId))
        {
            await _messageSender.SendCommandAsync(AuditLogContractMapper.ToTenant(normalized), cancellationToken);
        }
    }
}
