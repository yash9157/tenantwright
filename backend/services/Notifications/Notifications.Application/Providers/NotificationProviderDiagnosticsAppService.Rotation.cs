using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Providers;

namespace Notifications.Application.Providers;

public sealed partial class NotificationProviderDiagnosticsAppService
{
    public async Task<NotificationProviderSecretRotationStatusDto> GetSecretRotationStatusAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        tenantId = ResolveTenantScope(tenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(tenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(tenantId);
        var configs = await _configRepository.GetQueryableAsync();
        var query = configs.Where(x => x.TenantId == tenantId && x.SecretEnvelope != null);
        var total = await query.CountAsync(cancellationToken);
        var currentKeyId = _secretProtectionOptions?.Value.CurrentKeyId;
        var current = string.IsNullOrWhiteSpace(currentKeyId)
            ? 0
            : await query.CountAsync(x => x.SecretProtectionKeyId == currentKeyId, cancellationToken);
        return new NotificationProviderSecretRotationStatusDto
        {
            TenantId = tenantId,
            ProtectionConfigured = !string.IsNullOrWhiteSpace(currentKeyId),
            TotalSecretCount = total,
            CurrentKeySecretCount = current,
            RemainingSecretCount = total - current,
            IsComplete = !string.IsNullOrWhiteSpace(currentKeyId) && total == current
        };
    }

    public async Task<NotificationProviderLiveVerificationDto> RecordLiveVerificationAsync(
        Guid configurationId,
        RecordNotificationProviderLiveVerificationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (configurationId == Guid.Empty)
        {
            throw new ArgumentException("Provider configuration ID is required.", nameof(configurationId));
        }

        if (input.TenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID cannot be empty.", nameof(input));
        }

        var tenantId = ResolveTenantScope(input.TenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(tenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(tenantId);
        var configs = await _configRepository.GetQueryableAsync();
        var configuration = await configs.SingleOrDefaultAsync(
            x => x.Id == configurationId && x.TenantId == tenantId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Provider configuration was not found in the requested scope.");

        var verifiedAtUtc = DateTime.UtcNow;
        configuration.RecordLiveVerification(verifiedAtUtc, input.EvidenceReference);
        await _configRepository.UpdateAsync(configuration, autoSave: true, cancellationToken);

        return new NotificationProviderLiveVerificationDto
        {
            ConfigurationId = configuration.Id,
            TenantId = configuration.TenantId,
            Channel = configuration.Channel,
            VerificationState = configuration.VerificationState,
            VerifiedAtUtc = configuration.LiveVerifiedAtUtc!.Value,
            EvidenceReference = configuration.LiveVerificationReference!
        };
    }
}
