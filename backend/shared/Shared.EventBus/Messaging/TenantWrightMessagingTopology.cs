using System.Collections.Frozen;
using Shared.EventContracts;

namespace Shared.EventBus.Messaging;

/// <summary>
/// The single reviewed contract and endpoint policy for TenantWright messaging.
/// Hosts supply only the receive endpoints they own; they cannot redefine the
/// global route, producer, or endpoint ownership catalog.
/// </summary>
public static class TenantWrightMessagingTopology
{
    public const string AuditLogsPrincipal = "tenantwright-auditlogs";
    public const string AuthPrincipal = "tenantwright-auth";
    public const string IdentityPrincipal = "tenantwright-identity";
    public const string NotificationsPrincipal = "tenantwright-notifications";
    public const string PermissionsPrincipal = "tenantwright-permissions";
    public const string TenantPrincipal = "tenantwright-tenant";
    public const string WorkflowsPrincipal = "tenantwright-workflows";

    public const string AuditTenantEndpoint = "tenantwright-auditlogs-write-tenant-v1";
    public const string AuditHostEndpoint = "tenantwright-auditlogs-write-host-v1";
    public const string NotificationTenantEndpoint = "tenantwright-notifications-send-tenant-v1";
    public const string NotificationHostEndpoint = "tenantwright-notifications-send-host-v1";

    private static readonly string[] ServicePrincipals =
    [
        AuditLogsPrincipal, AuthPrincipal, IdentityPrincipal, NotificationsPrincipal, PermissionsPrincipal, TenantPrincipal,
        WorkflowsPrincipal
    ];

    private static readonly FrozenSet<string> ServicePrincipalSet = ServicePrincipals.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlyList<TenantWrightContractRoute> Routes =
    [
        Command<AuditLogWriteRequestedTenantEto>(AuditTenantEndpoint, ServicePrincipals),
        Command<AuditLogWriteRequestedHostEto>(AuditHostEndpoint, ServicePrincipals),
        Command<NotificationSendRequestedTenantEto>(NotificationTenantEndpoint,
            [IdentityPrincipal, PermissionsPrincipal, WorkflowsPrincipal]),
        Command<NotificationSendRequestedHostEto>(NotificationHostEndpoint,
            [IdentityPrincipal, PermissionsPrincipal, WorkflowsPrincipal]),
        Command<TenantInitialOwnerOnboardingRequestedEto>("tenantwright-workflows-tenant-initial-owner-onboarding-v1",
            [TenantPrincipal]),
        Command<TenantInitialOwnerOnboardingCompletedEto>("tenantwright-workflows-tenant-initial-owner-onboarding-v1",
            [TenantPrincipal]),
        Command<TenantOwnershipCancellationRequestedEto>("tenantwright-tenant-ownership-cancellation-v1",
            [WorkflowsPrincipal]),
        Event<IdentitySessionRevokedTenantEto>(IdentityPrincipal),
        Event<IdentitySessionRevokedHostEto>(IdentityPrincipal),
        Event<IdentityPlatformRoleRetirementRequestedEto>(IdentityPrincipal),
        Event<PermissionsPlatformRoleRetirementCompletedEto>(PermissionsPrincipal),
        Event<FeatureValuesChangedTenantEto>(PermissionsPrincipal),
        Event<FeatureValuesChangedHostEto>(PermissionsPrincipal),
        Event<PermissionGrantsChangedTenantEto>(PermissionsPrincipal),
        Event<PermissionGrantsChangedHostEto>(PermissionsPrincipal),
        Event<PermissionGrantApprovalRequestedEto>(PermissionsPrincipal),
        Event<PermissionGrantApprovalDecidedEto>(PermissionsPrincipal),
        Event<TenantAdminGrantsProvisionedEto>(PermissionsPrincipal),
        Event<TenantOwnershipInvitationRequestedEto>(TenantPrincipal),
        Event<TenantOwnershipInvitationCanceledEto>(TenantPrincipal),
        Event<TenantOwnershipInvitationCreatedEto>(IdentityPrincipal),
        Event<TenantOwnershipInvitationVerifiedEto>(IdentityPrincipal),
        Event<TenantOwnershipCompletedEto>(TenantPrincipal),
        Event<TenantDeactivatedEto>(TenantPrincipal),
        Event<TenantClosureChangedEto>(TenantPrincipal),
        Event<TenantDatabaseChangedEto>(TenantPrincipal),
        Event<TenantLifecycleChangedEto>(TenantPrincipal),
        Event<TenantOrganizationProfileChangedEto>(TenantPrincipal)
    ];

    public static IReadOnlyList<Type> PermissionCacheContracts { get; } =
    [
        typeof(FeatureValuesChangedTenantEto), typeof(FeatureValuesChangedHostEto),
        typeof(PermissionGrantsChangedTenantEto), typeof(PermissionGrantsChangedHostEto)
    ];

    private static readonly IReadOnlyList<TenantWrightEndpointRoute> Endpoints = CreateEndpoints();

    public static TenantWrightMessageCatalog CreateCatalog(params TenantWrightReceiveEndpoint[] localEndpoints) =>
        new(Routes, Endpoints, localEndpoints);

    public static TenantWrightMessageCatalog CreateCatalog(
        IEnumerable<TenantWrightContractRoute>? serviceRoutes,
        IEnumerable<TenantWrightEndpointRoute>? serviceEndpointRoutes,
        IEnumerable<TenantWrightReceiveEndpoint>? localEndpoints) =>
        new(
            Routes.Concat(serviceRoutes ?? []),
            Endpoints.Concat(serviceEndpointRoutes ?? []),
            localEndpoints ?? []);

    /// <summary>
    /// Every receive endpoint the reviewed topology assigns to <paramref name="ownerPrincipal"/>, including its
    /// permission-cache endpoint. Hosts consume exactly this set, so a host cannot drift from the topology.
    /// </summary>
    public static IReadOnlyList<TenantWrightReceiveEndpoint> ReceiveEndpointsOwnedBy(string ownerPrincipal)
    {
        if (!ServicePrincipalSet.Contains(ownerPrincipal))
            throw new InvalidOperationException("Receive endpoints require an approved service owner.");

        return Endpoints
            .Where(endpoint => string.Equals(endpoint.OwnerPrincipal, ownerPrincipal, StringComparison.Ordinal))
            .Select(endpoint => new TenantWrightReceiveEndpoint(endpoint.Name, endpoint.OwnerPrincipal, endpoint.ContractTypes))
            .ToArray();
    }

    public static string PermissionCacheEndpoint(string ownerPrincipal) =>
        ServicePrincipalSet.Contains(ownerPrincipal)
            ? $"{ownerPrincipal}-permission-cache-v1"
            : throw new InvalidOperationException("Permission cache endpoints require an approved service owner.");

    private static List<TenantWrightEndpointRoute> CreateEndpoints()
    {
        var endpoints = new List<TenantWrightEndpointRoute>
        {
            Endpoint(AuditTenantEndpoint, AuditLogsPrincipal, typeof(AuditLogWriteRequestedTenantEto)),
            Endpoint(AuditHostEndpoint, AuditLogsPrincipal, typeof(AuditLogWriteRequestedHostEto)),
            Endpoint(NotificationTenantEndpoint, NotificationsPrincipal, typeof(NotificationSendRequestedTenantEto)),
            Endpoint(NotificationHostEndpoint, NotificationsPrincipal, typeof(NotificationSendRequestedHostEto)),
            Endpoint("tenantwright-identity-ownership-invitation-v1", IdentityPrincipal,
                typeof(TenantOwnershipInvitationRequestedEto), typeof(TenantOwnershipInvitationCanceledEto)),
            Endpoint("tenantwright-identity-platform-role-retirement-completed-v1", IdentityPrincipal,
                typeof(PermissionsPlatformRoleRetirementCompletedEto)),
            Endpoint("tenantwright-identity-tenant-ownership-completed-v1", IdentityPrincipal,
                typeof(TenantOwnershipCompletedEto)),
            Endpoint("tenantwright-identity-tenant-deactivated-v1", IdentityPrincipal, typeof(TenantDeactivatedEto)),
            Endpoint("tenantwright-identity-tenant-closure-v1", IdentityPrincipal, typeof(TenantClosureChangedEto)),
            Endpoint("tenantwright-notifications-session-revoked-v1", NotificationsPrincipal,
                typeof(IdentitySessionRevokedTenantEto), typeof(IdentitySessionRevokedHostEto)),
            Endpoint("tenantwright-notifications-permission-approval-v1", NotificationsPrincipal,
                typeof(PermissionGrantApprovalRequestedEto), typeof(PermissionGrantApprovalDecidedEto)),
            Endpoint("tenantwright-notifications-tenant-closure-v1", NotificationsPrincipal, typeof(TenantClosureChangedEto)),
            Endpoint("tenantwright-auditlogs-tenant-database-changed-v1", AuditLogsPrincipal,
                typeof(TenantDatabaseChangedEto)),
            Endpoint("tenantwright-notifications-tenant-database-changed-v1", NotificationsPrincipal,
                typeof(TenantDatabaseChangedEto)),
            Endpoint("tenantwright-permissions-tenant-database-changed-v1", PermissionsPrincipal,
                typeof(TenantDatabaseChangedEto)),
            Endpoint("tenantwright-permissions-platform-role-retirement-v1", PermissionsPrincipal,
                typeof(IdentityPlatformRoleRetirementRequestedEto)),
            Endpoint("tenantwright-permissions-tenant-ownership-completed-v1", PermissionsPrincipal,
                typeof(TenantOwnershipCompletedEto)),
            Endpoint("tenantwright-tenant-admin-grants-provisioned-v1", TenantPrincipal,
                typeof(TenantAdminGrantsProvisionedEto)),
            Endpoint("tenantwright-tenant-ownership-invitation-created-v1", TenantPrincipal,
                typeof(TenantOwnershipInvitationCreatedEto)),
            Endpoint("tenantwright-tenant-ownership-invitation-verified-v1", TenantPrincipal,
                typeof(TenantOwnershipInvitationVerifiedEto)),
            Endpoint("tenantwright-tenant-ownership-cancellation-v1", TenantPrincipal,
                typeof(TenantOwnershipCancellationRequestedEto)),
            Endpoint("tenantwright-workflows-tenant-initial-owner-onboarding-v1", WorkflowsPrincipal,
                typeof(TenantInitialOwnerOnboardingRequestedEto), typeof(TenantInitialOwnerOnboardingCompletedEto))
        };

        endpoints.AddRange(ServicePrincipals.Select(principal =>
            new TenantWrightEndpointRoute(PermissionCacheEndpoint(principal), principal, PermissionCacheContracts)));
        return endpoints;
    }

    private static TenantWrightContractRoute Command<T>(string destination, IEnumerable<string> producers) where T : class =>
        new(typeof(T), TenantWrightMessageRouteKind.Command, destination,
            producers.ToFrozenSet(StringComparer.Ordinal));

    private static TenantWrightContractRoute Event<T>(params string[] producers) where T : class =>
        new(typeof(T), TenantWrightMessageRouteKind.Event, null, producers.ToFrozenSet(StringComparer.Ordinal));

    private static TenantWrightEndpointRoute Endpoint(string name, string owner, params Type[] contracts) =>
        new(name, owner, contracts);
}
