using Shared.Domain;
using Shared.Events;
using Shared.MultiTenancy;

namespace Shared.EventBus.Messaging;

/// <summary>
/// Transport-neutral contract rules shared by the outbound stamper and the inbound verifier: contract identity,
/// distributed metadata, the producer allow-list and the host-only versus tenant-required tenancy rules. Public so
/// every transport provider enforces exactly these rules instead of re-deriving them.
/// </summary>
public static class TenantWrightMessageContracts
{
    public const string HostScope = "host";
    public const string TenantScope = "tenant";

    public static DistributedContractAttribute GetMetadata(Type type) =>
        type.GetCustomAttributes(typeof(DistributedContractAttribute), false).Cast<DistributedContractAttribute>().SingleOrDefault()
        ?? throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

    public static string GetIdentity(Type type) =>
        type.GetCustomAttributes(typeof(EventNameAttribute), false).Cast<EventNameAttribute>().SingleOrDefault()?.Name
        ?? throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

    public static string ScopeOf(DistributedContractAttribute metadata) =>
        metadata.Scope == DistributedContractScope.TenantRequired ? TenantScope : HostScope;

    public static void EnsureAllowedOrigin(TenantWrightContractRoute route, string actor)
    {
        if (!route.AllowedOriginPrincipals.Contains(actor))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);
    }

    /// <summary>
    /// Outbound tenancy: a host-only contract is refused under an ambient tenant or with a tenant-owned body; a
    /// tenant-required contract needs an ambient tenant that exactly equals the body tenant.
    /// </summary>
    public static string? ResolveTenant(DistributedContractAttribute metadata, object message, ICurrentTenant currentTenant)
    {
        if (metadata.Scope == DistributedContractScope.HostOnly)
        {
            if (currentTenant.Id.HasValue || message is IMultiTenant)
                throw new MessagingSecurityException(MessagingSecurityException.TenantMismatch);
            return null;
        }

        if (!currentTenant.Id.HasValue || message is not IMultiTenant multiTenant || !multiTenant.TenantId.HasValue ||
            multiTenant.TenantId.Value != currentTenant.Id.Value)
            throw new MessagingSecurityException(MessagingSecurityException.TenantMismatch);
        return currentTenant.Id.Value.ToString("D").ToLowerInvariant();
    }

    /// <summary>
    /// Inbound tenancy: the signed origin scope and tenant must agree with the contract scope and, for a tenant
    /// contract, exactly with the body tenant. Returns the tenant to restore, or <see langword="null"/> for host scope.
    /// </summary>
    public static Guid? VerifyTenant(object message, DistributedContractAttribute metadata, OriginAttestation origin)
    {
        if (metadata.Scope == DistributedContractScope.HostOnly)
        {
            if (origin.Scope != HostScope || origin.TenantId is not null || message is IMultiTenant)
                throw new MessagingSecurityException(MessagingSecurityException.TenantMismatch);
            return null;
        }
        if (origin.Scope != TenantScope || origin.TenantId is null)
            throw new MessagingSecurityException(MessagingSecurityException.TenantMismatch);
        if (!Guid.TryParseExact(origin.TenantId, "D", out var tenantId))
            throw new MessagingSecurityException(MessagingSecurityException.TenantMismatch);
        if (!string.Equals(origin.TenantId, tenantId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            message is not IMultiTenant multiTenant || multiTenant.TenantId != tenantId)
            throw new MessagingSecurityException(MessagingSecurityException.TenantMismatch);
        return tenantId;
    }
}
