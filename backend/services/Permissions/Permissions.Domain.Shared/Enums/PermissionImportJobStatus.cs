namespace Permissions.Domain.Shared.Enums;

/// <summary>
/// Lifecycle of a durable permission-import job (BE-8). A job is created by validating an uploaded
/// payload; only a <see cref="Validated"/> job can be applied, and only via a one-time apply token.
/// Accepting an apply consumes the token and moves the job to <see cref="Applying"/>, where a
/// background worker re-validates and applies the retained snapshot. Terminal states are
/// <see cref="Applied"/>, <see cref="Failed"/>, and <see cref="Cancelled"/>.
/// </summary>
public enum PermissionImportJobStatus
{
    Validated = 1,
    Invalid = 2,
    Applied = 3,
    Failed = 4,
    Cancelled = 5,

    /// <summary>Apply was accepted and queued; the background worker owns the job from here.</summary>
    Applying = 6
}
