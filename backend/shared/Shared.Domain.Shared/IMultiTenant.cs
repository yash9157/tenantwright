namespace Shared.Domain;

public interface IMultiTenant
{
    Guid? TenantId { get; }
}
