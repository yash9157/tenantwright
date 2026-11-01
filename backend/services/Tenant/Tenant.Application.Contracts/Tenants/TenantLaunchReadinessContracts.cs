namespace Tenant.Application.Contracts.Tenants;

public enum TenantLaunchReadinessState
{
    Ready = 1,
    Blocked = 2
}

public sealed record TenantLaunchEvidenceDto(
    string Code,
    string Owner,
    bool IsReady,
    long? OwnerRevision,
    DateTimeOffset? ObservedAtUtc,
    string SafeMessage,
    string DeepLink);

public sealed record TenantLaunchReadinessDto(
    Guid TenantId,
    TenantLaunchReadinessState State,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<TenantLaunchEvidenceDto> Evidence,
    IReadOnlyList<string> Blockers);

public interface ITenantLaunchReadinessAppService
{
    Task<TenantLaunchReadinessDto> GetCurrentAsync(CancellationToken cancellationToken = default);
}
