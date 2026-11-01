using Permissions.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

public sealed class PermissionAssignmentOperation : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public PermissionAssignmentOperationStatus Status { get; private set; }
    public string? ResultPayload { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private PermissionAssignmentOperation() { }

    public PermissionAssignmentOperation(
        Guid id,
        Guid? tenantId,
        Guid actorUserId,
        string providerName,
        string providerKey,
        string idempotencyKey,
        string requestHash,
        DateTime createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        ActorUserId = actorUserId;
        ProviderName = providerName;
        ProviderKey = providerKey;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        Status = PermissionAssignmentOperationStatus.InProgress;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
    }

    public void Complete(string resultPayload, DateTime completedAtUtc)
    {
        ResultPayload = resultPayload;
        Status = PermissionAssignmentOperationStatus.Completed;
        CompletedAtUtc = DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc);
    }
}
