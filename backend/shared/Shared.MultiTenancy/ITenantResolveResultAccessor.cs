namespace Shared.MultiTenancy;

public interface ITenantResolveResultAccessor
{
    TenantResolveResult? Result { get; set; }
}
