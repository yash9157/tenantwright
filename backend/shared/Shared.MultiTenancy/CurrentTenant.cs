using Shared.Modularity.DependencyInjection;
using Shared.Modularity;
using Shared.Security;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;

namespace Shared.MultiTenancy;

public class CurrentTenant : ICurrentTenant, ISingletonDependency
{
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public virtual bool IsAvailable => Id.HasValue;

    public virtual Guid? Id => _currentTenantAccessor.Current is { } currentTenant
        ? currentTenant.TenantId
        : ClaimValueParser.ToGuidOrNull(
            FindFirstClaimValue(SharedClaimTypes.TenantId, SharedClaimTypes.LegacyTenantId));

    public virtual string? Name => _currentTenantAccessor.Current is { } currentTenant
        ? currentTenant.Name
        : FindFirstClaimValue(SharedClaimTypes.TenantName, SharedClaimTypes.LegacyTenantName);

    public CurrentTenant(
        ICurrentPrincipalAccessor principalAccessor,
        ICurrentTenantAccessor currentTenantAccessor)
    {
        _principalAccessor = principalAccessor;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public virtual IDisposable Change(Guid? id, string? name = null)
    {
        var parentScope = _currentTenantAccessor.Current;
        _currentTenantAccessor.Current = new BasicTenantInfo(id, name);
        return new DisposeAction(() =>
        {
            _currentTenantAccessor.Current = parentScope;
        });
    }

    private string? FindFirstClaimValue(params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var claim = _principalAccessor.Principal.Claims
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Type, claimType, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(candidate.Value));
            if (claim != null)
            {
                return claim.Value;
            }
        }

        return null;
    }
}

public interface ICurrentTenantAccessor
{
    BasicTenantInfo? Current { get; set; }
}

public class AsyncLocalCurrentTenantAccessor : ICurrentTenantAccessor, ISingletonDependency
{
    public static AsyncLocalCurrentTenantAccessor Instance { get; } = new();

    public BasicTenantInfo? Current
    {
        get => _currentTenant.Value;
        set => _currentTenant.Value = value;
    }

    private readonly AsyncLocal<BasicTenantInfo?> _currentTenant = new();
}

public class BasicTenantInfo
{
    public Guid? TenantId { get; }
    public string? Name { get; }

    public BasicTenantInfo(Guid? tenantId, string? name = null)
    {
        TenantId = tenantId;
        Name = name;
    }
}
