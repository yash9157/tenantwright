using Shared;
using Shared.Domain.Entities.Auditing;
using Tenant.Domain.Shared.Constants;
using Shared.AuditLogs.Auditing;
using Tenant.Domain.Shared.Enums;

namespace Tenant.Domain.Tenants;

[Audited]
public class Tenant : FullAuditedAggregateRoot<Guid>
{
    public string Name { get; private set; } = default!;
    public string NormalizedName { get; private set; } = default!;
    public bool IsActive { get; private set; } = true;
    public TenantLifecycleStatus LifecycleStatus { get; private set; } = TenantLifecycleStatus.Active;
    public long LifecycleRevision { get; private set; } = 1;
    public Guid? OwnerUserId { get; private set; }

    // Denormalized owner display, sourced from the Identity ownership-verified event
    // (Option A). Null for tenants whose ownership predates this denormalization; the
    // authoritative owner identity remains OwnerUserId.
    public string? OwnerDisplayName { get; private set; }
    public string? OwnerEmail { get; private set; }

    public string? ContactName { get; private set; }
    public string? ContactEmail { get; private set; }
    public string? ContactPhone { get; private set; }
    public string? AddressLine1 { get; private set; }
    public string? AddressLine2 { get; private set; }
    public string? City { get; private set; }
    public string? StateOrUnionTerritory { get; private set; }
    public string? PinCode { get; private set; }

    // Compliance / legal profile (additive, all nullable). India-first: TaxId is a GSTIN.
    public string? LegalEntityName { get; private set; }
    public string? RegistrationNumber { get; private set; }
    public string? TaxId { get; private set; }
    public string? Accreditation { get; private set; }
    public string? DataRegion { get; private set; }
    public int? RetentionPolicyDays { get; private set; }

    public List<TenantConnectionString> ConnectionStrings { get; private set; } = new();

    protected Tenant()
    {
    }

    public Tenant(Guid id, string name)
        : base(id)
    {
        SetName(name);
    }

    public string? FindConnectionString(string name)
    {
        name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);
        return ConnectionStrings.FirstOrDefault(x => x.Name == name)?.GetStoredValue();
    }

    public void SetConnectionString(string name, string value)
    {
        name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);
        value = Check.NotNullOrWhiteSpace(value, nameof(value));

        var existing = ConnectionStrings.FirstOrDefault(x => x.Name == name);
        if (existing != null)
        {
            existing.SetValue(value);
            return;
        }

        ConnectionStrings.Add(new TenantConnectionString(Id, name, value));
    }

    /// <summary>
    /// Assigns (or re-points) the tenant's dedicated database for <paramref name="name"/>: the protected connection
    /// string plus the fingerprint of the physical target it points at.
    /// </summary>
    public void SetProtectedConnectionString(
        string name,
        string protectedValue,
        string keyId,
        int keyVersion,
        string targetFingerprint)
    {
        name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);

        var existing = ConnectionStrings.FirstOrDefault(x => x.Name == name);
        if (existing != null)
        {
            existing.SetProtectedValue(protectedValue, keyId, keyVersion);
            existing.SetTargetFingerprint(targetFingerprint);
            return;
        }

        ConnectionStrings.Add(new TenantConnectionString(Id, name, protectedValue, keyId, keyVersion, targetFingerprint));
    }

    public string? FindConnectionTargetFingerprint(string name)
    {
        name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);
        return ConnectionStrings.FirstOrDefault(x => x.Name == name)?.TargetFingerprint;
    }

    public void RemoveConnectionString(string name)
    {
        name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);

        var existing = ConnectionStrings.FirstOrDefault(x => x.Name == name);
        if (existing != null)
        {
            ConnectionStrings.Remove(existing);
        }
    }

    public void SetName(string name)
    {
        name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxNameLength);
        Name = name;
        NormalizedName = NormalizeName(name);
    }

    public void SetOwner(Guid ownerUserId, string? ownerDisplayName = null, string? ownerEmail = null)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Tenant owner is required.", nameof(ownerUserId));
        OwnerUserId = ownerUserId;
        OwnerDisplayName = Normalize(ownerDisplayName, 256);
        OwnerEmail = Normalize(ownerEmail, 256);
    }

    public void SetOrganizationProfile(string? contactName, string? contactEmail, string? contactPhone, string? addressLine1, string? addressLine2, string? city, string? stateOrUnionTerritory, string? pinCode)
    {
        ContactName = Normalize(contactName, 128); ContactEmail = Normalize(contactEmail, 256); ContactPhone = Normalize(contactPhone, 32);
        AddressLine1 = Normalize(addressLine1, 256); AddressLine2 = Normalize(addressLine2, 256); City = Normalize(city, 128); StateOrUnionTerritory = Normalize(stateOrUnionTerritory, 64); PinCode = Normalize(pinCode, 6);
        if (!string.IsNullOrWhiteSpace(ContactEmail) && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(ContactEmail)) throw new ArgumentException("Contact email is invalid.");
        if (!string.IsNullOrWhiteSpace(PinCode) && (!System.Text.RegularExpressions.Regex.IsMatch(PinCode, "^[1-9][0-9]{5}$"))) throw new ArgumentException("Indian PIN code is invalid.");
    }

    public void SetLegalComplianceProfile(string? legalEntityName, string? registrationNumber, string? taxId, string? accreditation)
    {
        LegalEntityName = Normalize(legalEntityName, 200);
        RegistrationNumber = Normalize(registrationNumber, 64);
        TaxId = Normalize(taxId, 32);
        Accreditation = Normalize(accreditation, 128);
        if (!string.IsNullOrWhiteSpace(TaxId) && !System.Text.RegularExpressions.Regex.IsMatch(TaxId, "^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z]{1}[1-9A-Z]{1}Z[0-9A-Z]{1}$"))
        {
            throw new ArgumentException("Indian GSTIN (TaxId) is invalid.");
        }
    }

    public void SetDataGovernanceProfile(string? dataRegion, int? retentionPolicyDays)
    {
        DataRegion = Normalize(dataRegion, 32);
        if (retentionPolicyDays is < 1 or > 3650)
        {
            throw new ArgumentException("Retention policy days must be between 1 and 3650.");
        }
        RetentionPolicyDays = retentionPolicyDays;
    }

    public void BeginProvisioning()
    {
        if (OwnerUserId.HasValue || LifecycleStatus != TenantLifecycleStatus.Active)
            throw new InvalidOperationException("Only a new tenant without an owner can enter provisioning.");
        LifecycleStatus = TenantLifecycleStatus.Provisioning;
        IsActive = false;
        LifecycleRevision++;
    }

    public void Activate()
    {
        TransitionTo(TenantLifecycleStatus.Active);
    }

    public void Deactivate()
    {
        TransitionTo(TenantLifecycleStatus.Suspended);
    }

    public void TransitionTo(TenantLifecycleStatus status)
    {
        NormalizeLegacyInactiveStatus();
        if (LifecycleStatus == status)
        {
            return;
        }

        if (!CanTransitionTo(status))
        {
            throw new InvalidOperationException($"Tenant lifecycle transition from {LifecycleStatus} to {status} is not allowed.");
        }

        LifecycleStatus = status;
        IsActive = status == TenantLifecycleStatus.Active;
        LifecycleRevision++;
    }

    public IReadOnlyList<TenantLifecycleStatus> GetAllowedTransitions()
    {
        var currentStatus = GetEffectiveLifecycleStatus();
        return Enum.GetValues<TenantLifecycleStatus>()
            .Where(target => CanTransitionFrom(currentStatus, target))
            .ToArray();
    }

    public TenantLifecycleStatus GetEffectiveLifecycleStatus()
    {
        return !IsActive && LifecycleStatus == TenantLifecycleStatus.Active
            ? TenantLifecycleStatus.Suspended
            : LifecycleStatus;
    }

    public void RestoreAsSuspended()
    {
        IsDeleted = false;
        DeletionTime = null;
        DeleterId = null;
        LifecycleStatus = TenantLifecycleStatus.Suspended;
        IsActive = false;
        LifecycleRevision++;
    }

    /// <summary>
    /// Reverses a soft-delete, clearing the deletion audit markers. The tenant's
    /// active state is not changed here; callers restore it deactivated so
    /// reactivation is a conscious, separate step (fail-closed).
    /// </summary>
    public void Undelete()
    {
        IsDeleted = false;
        DeletionTime = null;
        DeleterId = null;
    }

    private bool CanTransitionTo(TenantLifecycleStatus target)
        => CanTransitionFrom(LifecycleStatus, target);

    private static bool CanTransitionFrom(TenantLifecycleStatus currentStatus, TenantLifecycleStatus target)
    {
        return currentStatus switch
        {
            TenantLifecycleStatus.Active => target is TenantLifecycleStatus.Suspended or TenantLifecycleStatus.Decommissioned,
            TenantLifecycleStatus.Provisioning => target is TenantLifecycleStatus.Active or TenantLifecycleStatus.Suspended or TenantLifecycleStatus.ProvisioningFailed or TenantLifecycleStatus.Decommissioned,
            TenantLifecycleStatus.Suspended => target is TenantLifecycleStatus.Active or TenantLifecycleStatus.Provisioning or TenantLifecycleStatus.Decommissioned,
            TenantLifecycleStatus.ProvisioningFailed => target is TenantLifecycleStatus.Provisioning or TenantLifecycleStatus.Decommissioned,
            TenantLifecycleStatus.Decommissioned => false,
            _ => false
        };
    }

    private void NormalizeLegacyInactiveStatus()
    {
        if (!IsActive && LifecycleStatus == TenantLifecycleStatus.Active)
        {
            LifecycleStatus = TenantLifecycleStatus.Suspended;
        }
    }

    private static string NormalizeName(string name)
    {
        return name.Trim().ToUpperInvariant();
    }
    private static string? Normalize(string? value, int maxLength) { var normalized = value?.Trim(); if (string.IsNullOrWhiteSpace(normalized)) return null; if (normalized.Length > maxLength) throw new ArgumentException("Profile value is too long."); return normalized; }
}
