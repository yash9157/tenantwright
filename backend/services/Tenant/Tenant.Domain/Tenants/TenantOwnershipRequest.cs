using Shared.Domain.Entities.Auditing;
using Tenant.Domain.Shared.Enums;

namespace Tenant.Domain.Tenants;

public sealed class TenantOwnershipRequest : CreationAuditedAggregateRoot<Guid>
{
    public const int MaxProvisioningRetryCount = 5;
    public static readonly TimeSpan MinimumProvisioningRetryInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumResendInterval = TimeSpan.FromMinutes(5);

    public Guid TenantId { get; private set; }
    public Guid? PreviousOwnerUserId { get; private set; }
    public string TargetEmail { get; private set; } = string.Empty;
    public string? SuggestedUserName { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public TenantOwnershipRequestKind Kind { get; private set; }
    public TenantOwnershipRequestStatus Status { get; private set; }
    public TenantOwnershipRequestStatus? WorkflowReconciledStatus { get; private set; }

    public void RecordWorkflowReconciliation(TenantOwnershipRequestStatus observedStatus)
    {
        if (Kind != TenantOwnershipRequestKind.InitialOwner || Status != observedStatus)
            throw new InvalidOperationException("Workflow reconciliation must match the initial-owner request state.");
        WorkflowReconciledStatus = observedStatus;
    }
    public Guid? TargetUserId { get; private set; }
    public Guid? IdentityInvitationId { get; private set; }
    public DateTime? InvitationCreatedAtUtc { get; private set; }
    public int ProvisioningRetryCount { get; private set; }
    public DateTime? LastProvisioningRetryAtUtc { get; private set; }
    public DateTime? PermissionsProvisionedAtUtc { get; private set; }
    public int ResendCount { get; private set; }
    public DateTime? LastResentAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public TenantRegistrationOrigin RegistrationOrigin { get; private set; } = TenantRegistrationOrigin.SuperAdmin;
    public Guid? RegistrationOperationId { get; private set; }

    private TenantOwnershipRequest() { }

    public TenantOwnershipRequest(Guid id, Guid tenantId, Guid? previousOwnerUserId, string targetEmail,
        string? suggestedUserName, string reasonCode, string? note, TenantOwnershipRequestKind kind, DateTime? expiresAtUtc = null,
        TenantRegistrationOrigin registrationOrigin = TenantRegistrationOrigin.SuperAdmin, Guid? registrationOperationId = null)
        : base(id)
    {
        TenantId = tenantId;
        PreviousOwnerUserId = previousOwnerUserId;
        TargetEmail = targetEmail;
        SuggestedUserName = suggestedUserName;
        ReasonCode = reasonCode;
        Note = note;
        Kind = kind;
        Status = TenantOwnershipRequestStatus.PendingInvitation;
        ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddDays(7);
        RegistrationOrigin = registrationOrigin;
        RegistrationOperationId = registrationOperationId;
    }

    public bool Complete(Guid targetUserId, DateTime completedAtUtc)
    {
        if (Status == TenantOwnershipRequestStatus.Completed) return false;
        if (Status is TenantOwnershipRequestStatus.Canceled or TenantOwnershipRequestStatus.Expired) return false;
        if (completedAtUtc > ExpiresAtUtc)
        {
            Status = TenantOwnershipRequestStatus.Expired;
            return false;
        }
        TargetUserId = targetUserId;
        CompletedAtUtc = completedAtUtc;
        Status = TenantOwnershipRequestStatus.Completed;
        return true;
    }

    public bool AcknowledgeIdentityInvitation(Guid invitationId, DateTime invitationCreatedAtUtc)
    {
        if (invitationId == Guid.Empty)
            throw new ArgumentException("Identity invitation id is required.", nameof(invitationId));

        if (IdentityInvitationId.HasValue)
        {
            if (IdentityInvitationId.Value != invitationId)
                throw new InvalidOperationException("A different Identity invitation is already associated with this ownership request.");

            return false;
        }

        if (Kind != TenantOwnershipRequestKind.InitialOwner)
            throw new InvalidOperationException("Only initial-owner provisioning requests accept an Identity creation acknowledgement.");
        if (Status != TenantOwnershipRequestStatus.PendingInvitation)
            return false;
        if (invitationCreatedAtUtc > ExpiresAtUtc)
        {
            Status = TenantOwnershipRequestStatus.Expired;
            return false;
        }

        IdentityInvitationId = invitationId;
        InvitationCreatedAtUtc = invitationCreatedAtUtc;
        Status = TenantOwnershipRequestStatus.AwaitingEmailVerification;
        return true;
    }

    public void RecordProvisioningRetry(DateTime utcNow, string reasonCode, string? note)
    {
        if (Kind != TenantOwnershipRequestKind.InitialOwner ||
            Status != TenantOwnershipRequestStatus.PendingInvitation ||
            IdentityInvitationId.HasValue)
            throw new InvalidOperationException("This provisioning request can no longer be retried.");

        if (ExpiresAtUtc <= utcNow)
        {
            Status = TenantOwnershipRequestStatus.Expired;
            throw new InvalidOperationException("Expired provisioning requests cannot be retried.");
        }

        if (ProvisioningRetryCount >= MaxProvisioningRetryCount)
            throw new InvalidOperationException("The provisioning retry limit has been reached.");

        if (LastProvisioningRetryAtUtc.HasValue &&
            LastProvisioningRetryAtUtc.Value.Add(MinimumProvisioningRetryInterval) > utcNow)
            throw new InvalidOperationException("The provisioning retry cooldown has not elapsed.");

        var normalizedReason = reasonCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReason) || normalizedReason.Length > 64)
            throw new ArgumentException("A provisioning retry reason of 64 characters or fewer is required.", nameof(reasonCode));

        var normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalizedNote?.Length > 500)
            throw new ArgumentException("A provisioning retry note must be 500 characters or fewer.", nameof(note));

        ProvisioningRetryCount++;
        LastProvisioningRetryAtUtc = utcNow;
    }

    public void RecordResend(DateTime utcNow)
    {
        ExpireIfDue(utcNow);
        if (Status is not (TenantOwnershipRequestStatus.PendingInvitation or TenantOwnershipRequestStatus.AwaitingEmailVerification))
        {
            throw new InvalidOperationException("Only active ownership requests can be resent.");
        }

        if (LastResentAtUtc.HasValue && LastResentAtUtc.Value.Add(MinimumResendInterval) > utcNow)
        {
            throw new InvalidOperationException("The ownership invitation resend cooldown has not elapsed.");
        }

        ResendCount++;
        LastResentAtUtc = utcNow;
    }

    public bool AcknowledgePermissionsProvisioned(Guid ownerUserId, DateTime provisionedAtUtc)
    {
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("Provisioned owner user id is required.", nameof(ownerUserId));
        if (PermissionsProvisionedAtUtc.HasValue)
            return false;
        if (Kind != TenantOwnershipRequestKind.InitialOwner ||
            Status != TenantOwnershipRequestStatus.Completed ||
            TargetUserId != ownerUserId)
            throw new InvalidOperationException("Permissions readiness does not match the completed initial-owner request.");

        PermissionsProvisionedAtUtc = provisionedAtUtc;
        return true;
    }

    public void Cancel()
    {
        if (Status != TenantOwnershipRequestStatus.Completed) Status = TenantOwnershipRequestStatus.Canceled;
    }

    public void ExpireIfDue(DateTime utcNow)
    {
        if ((Status is TenantOwnershipRequestStatus.PendingInvitation or TenantOwnershipRequestStatus.AwaitingEmailVerification) && ExpiresAtUtc <= utcNow)
            Status = TenantOwnershipRequestStatus.Expired;
    }
}
