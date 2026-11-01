using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

public sealed class PermissionAssignmentState : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public Guid Version { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private PermissionAssignmentState() { }

    public PermissionAssignmentState(Guid id, Guid? tenantId, string providerName, string providerKey, DateTime utcNow)
        : base(id)
    {
        TenantId = tenantId;
        ProviderName = providerName;
        ProviderKey = providerKey;
        Advance(utcNow);
    }

    public void Advance(DateTime utcNow)
    {
        Version = Guid.CreateVersion7();
        UpdatedAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }
}
