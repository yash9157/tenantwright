using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.FeatureManagement;

/// <summary>
/// Durable concurrency boundary and public version for one feature-value provider.
/// Every mutation for the provider must advance this row in the same transaction as its values.
/// </summary>
public sealed class FeatureProviderState : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public long Stamp { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private FeatureProviderState()
    {
    }

    public FeatureProviderState(Guid id, Guid? tenantId, string providerName, string providerKey)
        : base(id)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("Feature provider name is required.", nameof(providerName));
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ArgumentException("Feature provider key is required.", nameof(providerKey));
        }

        TenantId = tenantId;
        ProviderName = providerName.Trim();
        ProviderKey = providerKey.Trim();
        UpdatedAtUtc = DateTime.UnixEpoch;
    }

    public void Advance(DateTime utcNow)
    {
        Stamp = checked(Stamp + 1);
        UpdatedAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }
}
