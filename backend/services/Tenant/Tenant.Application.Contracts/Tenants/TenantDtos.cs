namespace Tenant.Application.Contracts.Tenants;

using Shared;
using Tenant.Domain.Shared.Enums;

public class TenantDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? LastModificationTime { get; set; }
}

/// <summary>
/// Host-facing detail projection of a tenant. Additive to <see cref="TenantDto"/>:
/// adds audit timestamps, the owner reference, an optimistic-concurrency stamp
/// (rotated on every mutating command), and the non-sensitive connection-string
/// status. The raw connection string is never included. Supports deleted-detail
/// reads so an operator can inspect a soft-deleted tenant before restoring it.
/// </summary>
public sealed class TenantDetailDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? LastModificationTime { get; set; }
    public DateTime? DeletionTime { get; set; }
    public string ConcurrencyStamp { get; set; } = default!;
    /// <summary>One entry per dedicatable service database: whether the tenant has its own, never the value.</summary>
    public IReadOnlyList<TenantDatabaseStatusDto> Databases { get; set; } = [];
}

/// <summary>
/// Restore command body. Restore is host-only and records a lifecycle-transition
/// history entry with this reason; the tenant is restored deactivated (Suspended)
/// so reactivation stays a conscious, separate step (fail-closed).
/// </summary>
public sealed class TenantRestoreInput
{
    public string ReasonCode { get; set; } = "restore";
    public string? Note { get; set; }
    public string? ConcurrencyStamp { get; set; }
}

public sealed class TenantRestoreResultDto
{
    public Guid TenantId { get; set; }
    public TenantLifecycleStatus PreviousStatus { get; set; }
    public TenantLifecycleStatus CurrentStatus { get; set; }
    public DateTime? RestoredAtUtc { get; set; }
    public bool WasAlreadyRestored { get; set; }
    public IReadOnlyList<string> AllowedNextActions { get; set; } = [];
}

public sealed class TenantRenameInput
{
    public string Name { get; set; } = string.Empty;
    public string ConcurrencyStamp { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? Note { get; set; }
}

public sealed class TenantArchiveInput
{
    public string TenantNameConfirmation { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class TenantArchiveResultDto
{
    public Guid TenantId { get; set; }
    public Guid? ClosureRequestId { get; set; }
    public TenantLifecycleStatus PreviousStatus { get; set; }
    public TenantLifecycleStatus CurrentStatus { get; set; }
    public bool UsedOverride { get; set; }
    public bool WasAlreadyArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
}

public class CurrentTenantProfileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
}
public sealed class TenantOrganizationProfileDto { public Guid TenantId { get; set; } public string? ContactName { get; set; } public string? ContactEmail { get; set; } public string? ContactPhone { get; set; } public string? AddressLine1 { get; set; } public string? AddressLine2 { get; set; } public string? City { get; set; } public string? StateOrUnionTerritory { get; set; } public string? PinCode { get; set; } public string CountryCode { get; set; } = "IN"; public string TimeZone { get; set; } = "Asia/Kolkata"; public string Locale { get; set; } = "en-IN"; public string DisplayCurrency { get; set; } = "INR"; public string ConcurrencyStamp { get; set; } = string.Empty; }
public sealed class TenantOrganizationProfileUpdateInput { public string? ContactName { get; set; } public string? ContactEmail { get; set; } public string? ContactPhone { get; set; } public string? AddressLine1 { get; set; } public string? AddressLine2 { get; set; } public string? City { get; set; } public string? StateOrUnionTerritory { get; set; } public string? PinCode { get; set; } public string? ConcurrencyStamp { get; set; } }

public sealed class TenantLegalComplianceProfileDto { public Guid TenantId { get; set; } public string? LegalEntityName { get; set; } public string? RegistrationNumber { get; set; } public string? TaxId { get; set; } public string? Accreditation { get; set; } public string ConcurrencyStamp { get; set; } = string.Empty; }
public sealed class TenantLegalComplianceProfileUpdateInput { public string? LegalEntityName { get; set; } public string? RegistrationNumber { get; set; } public string? TaxId { get; set; } public string? Accreditation { get; set; } public string? ConcurrencyStamp { get; set; } }
public sealed class TenantDataGovernanceProfileDto { public Guid TenantId { get; set; } public string? DataRegion { get; set; } public int? RetentionPolicyDays { get; set; } public string ConcurrencyStamp { get; set; } = string.Empty; }
public sealed class TenantDataGovernanceProfileUpdateInput { public string? DataRegion { get; set; } public int? RetentionPolicyDays { get; set; } public string? ConcurrencyStamp { get; set; } }

public class TenantCreateDto
{
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

public sealed class TenantCreateWithInitialOwnerInput
{
    public string Name { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string? SuggestedUserName { get; set; }
    public string ReasonCode { get; set; } = "initial-owner";
    public string? Note { get; set; }
}

/// <summary>
/// Auth-to-Tenant integration request. This contract is never exposed to a
/// browser; it is accepted only by the TenantInternalProvisioning policy.
/// </summary>
public sealed class PublicTenantRegistrationInitiateInput
{
    public Guid OperationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string? SuggestedUserName { get; set; }
}

public sealed class PublicTenantRegistrationInitiateResult
{
    public Guid TenantId { get; set; }
    public Guid OwnershipRequestId { get; set; }
    public bool IsExistingOperation { get; set; }
}

/// <summary>
/// Secret- and PII-free progress projection for an initial-owner provisioning
/// operation. It intentionally excludes owner email, invitation token/link,
/// delivery content, and tenant connection strings.
/// </summary>
public sealed class TenantProvisioningOperationDto
{
    public Guid TenantId { get; set; }
    public Guid OwnershipRequestId { get; set; }
    public Guid? RegistrationOperationId { get; set; }
    public Guid? IdentityInvitationId { get; set; }
    public TenantRegistrationOrigin RegistrationOrigin { get; set; }
    public TenantOwnershipRequestStatus Status { get; set; }
    public TenantLifecycleStatus TenantLifecycleStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? InvitationCreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int ProvisioningRetryCount { get; set; }
    public DateTime? LastProvisioningRetryAtUtc { get; set; }
    public DateTime? PermissionsProvisionedAtUtc { get; set; }
    public DateTime? RetryAvailableAtUtc { get; set; }
    public bool CanRetry { get; set; }
    public bool IsReady { get; set; }
    public bool IsTerminal { get; set; }
}

public sealed class TenantProvisioningRetryInput
{
    public string ReasonCode { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class TenantOwnershipRequestDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string TargetEmail { get; set; } = string.Empty;
    public TenantOwnershipRequestKind Kind { get; set; }
    public TenantOwnershipRequestStatus Status { get; set; }
    public Guid? PreviousOwnerUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public TenantRegistrationOrigin RegistrationOrigin { get; set; }
    public Guid? RegistrationOperationId { get; set; }
    public int ResendCount { get; set; }
    public DateTime? LastResentAtUtc { get; set; }
    public DateTime? ResendAvailableAtUtc { get; set; }
}

public sealed class GetTenantOwnershipHistoryInput
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public sealed class TenantClosureRequestInput { public string ReasonCode { get; set; } = string.Empty; public string? Note { get; set; } }
public sealed class TenantClosureActionInput { public string ReasonCode { get; set; } = string.Empty; public string? Note { get; set; } }
public sealed class GetTenantClosureHistoryInput { public int SkipCount { get; set; } public int MaxResultCount { get; set; } = 50; }
public sealed class TenantClosureRequestDto { public Guid Id { get; set; } public Guid TenantId { get; set; } public TenantClosureStatus Status { get; set; } public string ReasonCode { get; set; } = string.Empty; public string? Note { get; set; } public Guid RequestedByUserId { get; set; } public Guid? ApprovedByUserId { get; set; } public Guid? RejectedByUserId { get; set; } public DateTime CreationTime { get; set; } public DateTime? RejectedAtUtc { get; set; } public string? DecisionReasonCode { get; set; } public string? DecisionNote { get; set; } public DateTime? ClosedAtUtc { get; set; } public DateTime? RestoredAtUtc { get; set; } }
public sealed class TenantClosureStatusDto { public Guid RequestId { get; set; } public TenantClosureStatus Status { get; set; } public string ReasonCode { get; set; } = string.Empty; public string? Note { get; set; } public DateTime CreationTime { get; set; } public DateTime? RejectedAtUtc { get; set; } public string? DecisionReasonCode { get; set; } public string? DecisionNote { get; set; } public DateTime? ClosedAtUtc { get; set; } public DateTime? RestoredAtUtc { get; set; } }

public sealed class TenantOwnershipTransferInput
{
    public string TargetEmail { get; set; } = string.Empty;
    public string? SuggestedUserName { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class TenantUpdateDto
{
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>TenantAppService.ApplySort</c>. <c>Owner</c> orders by the denormalized
/// owner display name; the declared name stays stable regardless of that storage detail.
/// </remarks>
[SortableFields(
    "Name",
    "CreationTime",
    "LifecycleStatus",
    "Owner",
    DefaultSort = "Name")]
public class GetTenantsInput
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
    public string? Filter { get; set; }
    public bool IncludeDeleted { get; set; }

    // Additive server-side filters (all optional; null/empty means "no constraint").
    // Lifecycle is matched against the effective status surfaced by list rows, so
    // legacy inactive rows stored as Active are filtered as Suspended. Values must
    // be defined enum members.
    public List<TenantLifecycleStatus>? LifecycleStatuses { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public bool? HasDedicatedDatabase { get; set; }
    public Guid? OwnerUserId { get; set; }

    // Additive onboarding-stage filter (C3 drill target). Null means "no constraint".
    // In-progress stages match tenants whose latest initial-owner provisioning request
    // is currently sitting at that milestone; Active matches live tenants.
    public TenantProvisioningStage? ProvisioningStage { get; set; }

    /// <summary>
    /// Optional sort in <c>"Field"</c> or <c>"Field desc"</c> form, validated against the declared set
    /// above. A bare field name means ascending. Omit for the declared default.
    /// </summary>
    public string? Sorting { get; set; }
}

/// <summary>
/// Bounded host-console lookup used by tenant selectors. The contract is
/// intentionally smaller than <see cref="TenantListItemDto"/> so a picker does
/// not receive owner, connection, lifecycle-history, or action data.
/// </summary>
public sealed class GetTenantSelectionOptionsInput
{
    public string? Search { get; set; }
    public int MaxResultCount { get; set; } = 20;
    public bool IncludeInactive { get; set; } = true;
}

public sealed class TenantSelectionOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
}

/// <summary>
/// Onboarding pipeline stage for the tenant-list drill (C3). Mirrors the funnel
/// stage keys. The in-progress stages classify a tenant by the furthest milestone
/// its initial-owner provisioning request reached without advancing; Active is a
/// live tenant.
/// </summary>
public enum TenantProvisioningStage
{
    Created = 0,
    InvitationSent = 1,
    OwnerVerified = 2,
    PermissionsProvisioned = 3,
    Active = 4
}

/// <summary>
/// Non-sensitive owner projection for a tenant list row. Owner display data is
/// Identity-owned; Tenant denormalizes it from the ownership-verified event, so
/// <see cref="DisplayName"/>/<see cref="Email"/> may be null for tenants whose
/// ownership predates that denormalization (the FE renders a placeholder).
/// </summary>
public sealed class TenantOwnerSummaryDto
{
    public Guid? UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}

/// <summary>
/// Host list-row projection of a tenant. Additive superset of <see cref="TenantDto"/>:
/// adds the owner summary, the non-sensitive connection status (<c>IsConfigured</c>
/// only on the list — never the fingerprint or secret), and the server-computed
/// <see cref="AllowedActions"/> capability tokens so the FE renders actions from
/// server truth instead of inferring them from status combinations.
/// </summary>
public sealed class TenantListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? LastModificationTime { get; set; }
    public TenantOwnerSummaryDto? Owner { get; set; }
    public bool HasDedicatedDatabase { get; set; }
    public IReadOnlyList<string> AllowedActions { get; set; } = [];
}

public class TenantConfigurationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; }
    public Dictionary<string, string> ConnectionStrings { get; set; } = new();
}

public sealed class TenantServiceabilityBatchRequest
{
    public List<Guid> TenantIds { get; init; } = [];
}

public sealed class TenantServiceabilityDto
{
    public required Guid TenantId { get; init; }
    public required bool Exists { get; init; }
    public required bool IsServiceable { get; init; }
    public TenantLifecycleStatus? LifecycleStatus { get; init; }
    public long? LifecycleRevision { get; init; }
}

public sealed class TenantServiceabilityBatchResult
{
    public List<TenantServiceabilityDto> Tenants { get; init; } = [];
}

/// <summary>
/// Non-sensitive status of one service database for a tenant. Tenants share each service's database by default;
/// <see cref="IsDedicated"/> is true only when the tenant opted in to its own database for <see cref="Name"/>.
/// Never carries the connection string: only a non-reversible fingerprint (a short hash prefix) that lets an
/// operator tell two configured values apart without exposing the secret.
/// </summary>
public sealed class TenantDatabaseStatusDto
{
    /// <summary>The service database name from the reviewed catalog, for example <c>Notifications</c>.</summary>
    public string Name { get; set; } = default!;

    public bool IsDedicated { get; set; }

    public string? Fingerprint { get; set; }
}

/// <summary>
/// Every dedicatable service database of a tenant, plus the tenant concurrency stamp to send with a change.
/// </summary>
public sealed class TenantDatabaseListDto
{
    public IReadOnlyList<TenantDatabaseStatusDto> Items { get; set; } = [];

    public string ConcurrencyStamp { get; set; } = default!;
}

/// <summary>
/// Result of a host-initiated connectivity check against a dedicated database. Carries only a boolean outcome plus
/// a safe, non-sensitive message — never the connection string or the driver's message.
/// </summary>
public sealed class TenantDatabaseTestResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = default!;
}

/// <summary>
/// Assigns or re-points a tenant's dedicated database for one service database name.
/// <para>
/// Assigning or changing a dedicated database never moves data. From the moment the change commits, the service
/// reads and writes only the new target for this tenant; rows the tenant already has in the shared database (or in a
/// previously assigned database) stay where they are and become unreachable to it. Every assignment, first-time or
/// changed, therefore requires <see cref="AcknowledgeNoDataMigration"/>.
/// </para>
/// </summary>
public sealed class SetTenantDatabaseInput
{
    /// <summary>Write-only PostgreSQL connection string. Protected at rest; never echoed or logged.</summary>
    public string ConnectionString { get; set; } = default!;

    /// <summary>Open a connection with the supplied value first; nothing is saved when it fails.</summary>
    public bool TestBeforeSave { get; set; }

    /// <summary>Optional optimistic-concurrency stamp from <see cref="TenantDatabaseListDto.ConcurrencyStamp"/>.</summary>
    public string? ConcurrencyStamp { get; set; }

    /// <summary>
    /// Required to be true when the tenant already has a dedicated database for the name: the caller confirms that
    /// no existing data is copied or moved to the new target.
    /// </summary>
    public bool AcknowledgeNoDataMigration { get; set; }
}

/// <summary>
/// Removes a tenant's dedicated database for one name, returning the tenant to the shared service database. Like any
/// change of an existing assignment it moves no data, so <see cref="AcknowledgeNoDataMigration"/> must be true.
/// </summary>
public sealed class RemoveTenantDatabaseInput
{
    public string? ConcurrencyStamp { get; set; }

    public bool AcknowledgeNoDataMigration { get; set; }
}

/// <summary>What a set request did to the tenant's assignment.</summary>
public enum TenantDatabaseAssignmentChange
{
    /// <summary>The pre-save connectivity test failed; nothing was saved.</summary>
    NotSaved = 0,

    /// <summary>The tenant moved from the shared database to a dedicated one. No data was moved.</summary>
    Assigned = 1,

    /// <summary>The tenant's dedicated database was re-pointed to a new target. No data was moved.</summary>
    Reassigned = 2,

    /// <summary>The value was re-saved for the same target (for example new credentials).</summary>
    Updated = 3
}

/// <summary>
/// Result of setting a dedicated database: the safe post-update status, what changed, and the optional pre-save test
/// outcome. No raw secret. <see cref="DataMigrated"/> is always false: assignments never copy or move data.
/// </summary>
public sealed class SetTenantDatabaseResultDto
{
    public TenantDatabaseStatusDto Status { get; set; } = new();

    public TenantDatabaseAssignmentChange Change { get; set; }

    public bool DataMigrated { get; set; }

    public TenantDatabaseTestResultDto? TestResult { get; set; }
}

public sealed class TenantLifecycleTransitionInput
{
    public string ReasonCode { get; set; } = default!;
    public string? Note { get; set; }
}

public sealed class TenantLifecycleTransitionDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public TenantLifecycleStatus PreviousStatus { get; set; }
    public TenantLifecycleStatus CurrentStatus { get; set; }
    public string ReasonCode { get; set; } = default!;
    public string? Note { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public Guid? ActorUserId { get; set; }
}

public sealed class GetTenantLifecycleHistoryInput
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

/// <summary>
/// Discriminator for a merged tenant-history entry (P5). Identifies which
/// underlying per-domain record a <see cref="TenantHistoryEntryDto"/> came from.
/// </summary>
public enum TenantHistoryEntryType
{
    LifecycleTransition = 1,
    OwnershipRequest = 2,
    ClosureRequest = 3
}

/// <summary>
/// One entry in the merged, time-ordered tenant history (P5). A flat,
/// discriminated projection over lifecycle transitions, ownership requests, and
/// closure requests. Contact PII (e.g. ownership target email) is deliberately
/// omitted from this timeline; use the dedicated ownership history for that.
/// Status/kind values are normalized enum names (strings) since the sources are
/// heterogeneous. Actor names are not resolved here (the FE composes them from
/// <see cref="ActorUserId"/> via Identity).
/// </summary>
public sealed class TenantHistoryEntryDto
{
    public TenantHistoryEntryType EntryType { get; set; }
    public Guid SourceId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Status { get; set; }
    public string? PreviousStatus { get; set; }
    public string? Kind { get; set; }
    public string? ReasonCode { get; set; }
    public string? Note { get; set; }
}

public sealed class GetTenantHistoryInput
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;

    /// <summary>Optional filter to a single source type; null returns all.</summary>
    public TenantHistoryEntryType? EntryType { get; set; }
}

public sealed class GetTenantHistoryCursorInput
{
    public int MaxResultCount { get; set; } = 50;
    public TenantHistoryEntryType? EntryType { get; set; }
    public string? Cursor { get; set; }
}

public sealed class TenantHistoryCursorPageDto
{
    public IReadOnlyList<TenantHistoryEntryDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public enum TenantActionImpactAction
{
    Suspend = 1,
    Close = 2,
    Archive = 3,
    Activate = 4
}

public enum TenantImpactDependencyStatus
{
    Ok = 1,
    Unavailable = 2
}

public sealed class TenantActionImpactDto
{
    public Guid TenantId { get; set; }
    public TenantActionImpactAction Action { get; set; }
    public TenantLifecycleStatus CurrentStatus { get; set; }
    public TenantLifecycleStatus TargetStatus { get; set; }
    public bool HasOwner { get; set; }
    public int DedicatedDatabaseCount { get; set; }
    public int ActiveOwnershipRequestCount { get; set; }
    public int ActiveClosureRequestCount { get; set; }
    public IReadOnlyList<string> Preconditions { get; set; } = [];
    public IReadOnlyList<string> Blockers { get; set; } = [];
    public IReadOnlyList<string> Consequences { get; set; } = [];
    public int? ActiveSessionCount { get; set; }
    public TenantImpactDependencyStatus IdentityStatus { get; set; }
    public int? PendingNotificationCount { get; set; }
    public TenantImpactDependencyStatus NotificationsStatus { get; set; }
}

/// <summary>
/// One-call operational overview of a tenant for host support (P5). Read-only
/// and secret-free: connection status is a boolean only (never the value or
/// fingerprint).
/// </summary>
public sealed class TenantOperationalSummaryDto
{
    public Guid TenantId { get; set; }
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
    public Guid? OwnerUserId { get; set; }
    public int DedicatedDatabaseCount { get; set; }
    public int ActiveOwnershipRequestCount { get; set; }
    public int ActiveClosureRequestCount { get; set; }
    public DateTime? LastLifecycleTransitionAtUtc { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? LastModificationTime { get; set; }
    public IReadOnlyList<TenantLifecycleStatus> AllowedLifecycleTransitions { get; set; } = [];
    public bool CanArchive { get; set; }
    public string? ArchiveBlockedReason { get; set; }
    public Guid? LatestClosureRequestId { get; set; }
    public TenantClosureStatus? LatestClosureStatus { get; set; }
    public bool CanRestore { get; set; }
}
