using System;

namespace Shared.MultiTenancy;

public interface ICurrentTenant
{
    bool IsAvailable { get; }

    Guid? Id { get; }

    string? Name { get; }

    IDisposable Change(Guid? id, string? name = null);
}

public enum MultiTenancySides
{
    Tenant = 1,
    Host = 2,
    Both = 3
}

public static class CurrentTenantExtensions
{
    public static MultiTenancySides GetMultiTenancySide(this ICurrentTenant currentTenant)
    {
        return currentTenant.Id.HasValue
            ? MultiTenancySides.Tenant
            : MultiTenancySides.Host;
    }
}
