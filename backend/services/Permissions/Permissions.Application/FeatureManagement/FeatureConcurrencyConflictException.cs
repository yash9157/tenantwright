using Permissions.Application.Contracts.FeatureManagement;

namespace Permissions.Application.FeatureManagement;

/// <summary>
/// Thrown when a feature update is submitted against a stale provider stamp. The controller maps it
/// to HTTP 409 with the carried <see cref="FeatureConcurrencyConflictDto"/> so the editor can reload.
/// </summary>
public sealed class FeatureConcurrencyConflictException : Exception
{
    public FeatureConcurrencyConflictDto Conflict { get; }

    public FeatureConcurrencyConflictException(FeatureConcurrencyConflictDto conflict)
        : base($"Feature update concurrency conflict: {conflict.Code}.")
    {
        Conflict = conflict;
    }
}
