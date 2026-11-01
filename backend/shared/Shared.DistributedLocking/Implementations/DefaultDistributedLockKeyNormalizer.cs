using Microsoft.Extensions.Options;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;

namespace Shared.DistributedLocking.Implementations;

public sealed class DefaultDistributedLockKeyNormalizer : IDistributedLockKeyNormalizer
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IOptionsMonitor<DistributedLockingOptions> _optionsMonitor;

    public DefaultDistributedLockKeyNormalizer(
        ICurrentTenant currentTenant,
        IOptionsMonitor<DistributedLockingOptions> optionsMonitor)
    {
        _currentTenant = currentTenant;
        _optionsMonitor = optionsMonitor;
    }

    public string NormalizeKey(DistributedLockKeyNormalizeArgs args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Name);

        var options = _optionsMonitor.CurrentValue;
        var keyPrefix = options.KeyPrefix ?? string.Empty;
        var key = $"{keyPrefix}{args.Name.Trim()}";

        if (args.IgnoreTenant)
        {
            return $"t:global,l:{key}";
        }

        var tenantId = _currentTenant.Id;
        return tenantId.HasValue
            ? $"t:{tenantId.Value:D},l:{key}"
            : $"t:host,l:{key}";
    }
}
