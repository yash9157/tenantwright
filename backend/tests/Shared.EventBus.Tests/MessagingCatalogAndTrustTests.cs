using System.Reflection;
using System.Text;
using MassTransit;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class MessagingCatalogAndTrustTests
{
    private static readonly string[] ApprovedServicePrincipals =
    [
        TenantWrightMessagingTopology.AuditLogsPrincipal,
        TenantWrightMessagingTopology.AuthPrincipal,
        TenantWrightMessagingTopology.IdentityPrincipal,
        TenantWrightMessagingTopology.NotificationsPrincipal,
        TenantWrightMessagingTopology.PermissionsPrincipal,
        TenantWrightMessagingTopology.TenantPrincipal,
        TenantWrightMessagingTopology.WorkflowsPrincipal
    ];

    [Fact]
    public void Production_topology_should_freeze_audit_command_ownership_and_permission_fanout()
    {
        var catalog = TenantWrightMessagingTopology.CreateCatalog();

        var tenantAudit = catalog.GetRoute(typeof(AuditLogWriteRequestedTenantEto));
        Assert.Equal(TenantWrightMessageRouteKind.Command, tenantAudit.Kind);
        Assert.Equal(TenantWrightMessagingTopology.AuditTenantEndpoint, tenantAudit.CommandDestination);
        Assert.Contains(TenantWrightMessagingTopology.AuthPrincipal, tenantAudit.AllowedOriginPrincipals);
        Assert.Contains(TenantWrightMessagingTopology.AuditLogsPrincipal, tenantAudit.AllowedOriginPrincipals);

        var auditEndpoint = catalog.GetEndpointRoute(TenantWrightMessagingTopology.AuditTenantEndpoint);
        Assert.Equal(TenantWrightMessagingTopology.AuditLogsPrincipal, auditEndpoint.OwnerPrincipal);
        Assert.Equal([typeof(AuditLogWriteRequestedTenantEto)], auditEndpoint.ContractTypes);

        foreach (var principal in ApprovedServicePrincipals)
        {
            var endpoint = catalog.GetEndpointRoute(TenantWrightMessagingTopology.PermissionCacheEndpoint(principal));
            Assert.Equal(principal, endpoint.OwnerPrincipal);
            Assert.Equal(TenantWrightMessagingTopology.PermissionCacheContracts, endpoint.ContractTypes);
        }

        var permissionCacheOwners = catalog.EndpointRoutes
            .Where(endpoint => endpoint.Name.EndsWith("-permission-cache-v1", StringComparison.Ordinal))
            .Select(endpoint => endpoint.OwnerPrincipal)
            .Order(StringComparer.Ordinal);
        Assert.Equal(ApprovedServicePrincipals.Order(StringComparer.Ordinal), permissionCacheOwners);
    }

    [Fact]
    public void Receive_endpoints_owned_by_a_principal_should_be_exactly_its_topology_endpoints()
    {
        var catalog = TenantWrightMessagingTopology.CreateCatalog();

        foreach (var principal in ApprovedServicePrincipals)
        {
            var owned = TenantWrightMessagingTopology.ReceiveEndpointsOwnedBy(principal);
            var declared = catalog.EndpointRoutes
                .Where(endpoint => string.Equals(endpoint.OwnerPrincipal, principal, StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(declared.Select(endpoint => endpoint.Name), owned.Select(endpoint => endpoint.Name));
            Assert.All(owned, endpoint =>
            {
                Assert.Equal(principal, endpoint.OwnerPrincipal);
                Assert.Equal(catalog.GetEndpointRoute(endpoint.Name).ContractTypes, endpoint.ContractTypes);
            });
            Assert.Contains(owned, endpoint =>
                endpoint.Name == TenantWrightMessagingTopology.PermissionCacheEndpoint(principal) &&
                endpoint.ContractTypes.SequenceEqual(TenantWrightMessagingTopology.PermissionCacheContracts));

            // The owned set is a valid local endpoint set for the production catalog.
            Assert.Equal(owned.Count, TenantWrightMessagingTopology.CreateCatalog([.. owned]).ReceiveEndpoints.Count);
        }
    }

    [Theory]
    [InlineData("tenantwright-orders")]
    [InlineData("tenantwright-attacker")]
    [InlineData("TENANTWRIGHT-IDENTITY")]
    [InlineData("")]
    public void Receive_endpoints_should_reject_an_unapproved_owner(string ownerPrincipal)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TenantWrightMessagingTopology.ReceiveEndpointsOwnedBy(ownerPrincipal));

        Assert.Equal("Receive endpoints require an approved service owner.", exception.Message);
    }

    [Theory]
    [InlineData("tenantwright-orders")]
    [InlineData("tenantwright-attacker")]
    [InlineData("TENANTWRIGHT-IDENTITY")]
    [InlineData("")]
    public void Permission_cache_endpoint_should_reject_an_unapproved_owner(string ownerPrincipal)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TenantWrightMessagingTopology.PermissionCacheEndpoint(ownerPrincipal));

        Assert.Equal("Permission cache endpoints require an approved service owner.", exception.Message);
    }

    [Fact]
    public void Every_approved_service_principal_should_have_audit_and_permission_cache_transport_authority()
    {
        var catalog = TenantWrightMessagingTopology.CreateCatalog();
        var tenantAudit = catalog.GetRoute(typeof(AuditLogWriteRequestedTenantEto));
        var hostAudit = catalog.GetRoute(typeof(AuditLogWriteRequestedHostEto));

        Assert.Equal(
            ApprovedServicePrincipals.Order(StringComparer.Ordinal),
            tenantAudit.AllowedOriginPrincipals.Order(StringComparer.Ordinal));
        Assert.Equal(
            ApprovedServicePrincipals.Order(StringComparer.Ordinal),
            hostAudit.AllowedOriginPrincipals.Order(StringComparer.Ordinal));
        Assert.Equal(TenantWrightMessagingTopology.AuditHostEndpoint, hostAudit.CommandDestination);
    }

    [Fact]
    public void Initial_owner_onboarding_should_have_one_workflows_command_owner_and_one_tenant_cancellation_owner()
    {
        var catalog = TenantWrightMessagingTopology.CreateCatalog();

        var start = catalog.GetRoute(typeof(TenantInitialOwnerOnboardingRequestedEto));
        Assert.Equal(TenantWrightMessageRouteKind.Command, start.Kind);
        Assert.Equal("tenantwright-workflows-tenant-initial-owner-onboarding-v1", start.CommandDestination);
        Assert.Equal([TenantWrightMessagingTopology.TenantPrincipal], start.AllowedOriginPrincipals);

        var completion = catalog.GetRoute(typeof(TenantInitialOwnerOnboardingCompletedEto));
        Assert.Equal(TenantWrightMessageRouteKind.Command, completion.Kind);
        Assert.Equal(start.CommandDestination, completion.CommandDestination);
        Assert.Equal([TenantWrightMessagingTopology.TenantPrincipal], completion.AllowedOriginPrincipals);

        var cancellation = catalog.GetRoute(typeof(TenantOwnershipCancellationRequestedEto));
        Assert.Equal(TenantWrightMessageRouteKind.Command, cancellation.Kind);
        Assert.Equal("tenantwright-tenant-ownership-cancellation-v1", cancellation.CommandDestination);
        Assert.Equal([TenantWrightMessagingTopology.WorkflowsPrincipal], cancellation.AllowedOriginPrincipals);

        var workflows = catalog.GetEndpointRoute(start.CommandDestination!);
        Assert.Equal(TenantWrightMessagingTopology.WorkflowsPrincipal, workflows.OwnerPrincipal);
        Assert.Contains(typeof(TenantInitialOwnerOnboardingRequestedEto), workflows.ContractTypes);
        Assert.Contains(typeof(TenantInitialOwnerOnboardingCompletedEto), workflows.ContractTypes);
    }

    [Fact]
    public void Notification_commands_should_accept_only_the_approved_notification_producers()
    {
        var catalog = TenantWrightMessagingTopology.CreateCatalog();
        string[] approvedProducers =
        [
            TenantWrightMessagingTopology.IdentityPrincipal,
            TenantWrightMessagingTopology.PermissionsPrincipal,
            TenantWrightMessagingTopology.WorkflowsPrincipal
        ];

        foreach (var contract in new[] { typeof(NotificationSendRequestedTenantEto), typeof(NotificationSendRequestedHostEto) })
        {
            var route = catalog.GetRoute(contract);
            Assert.Equal(TenantWrightMessageRouteKind.Command, route.Kind);
            Assert.Equal(
                approvedProducers.Order(StringComparer.Ordinal),
                route.AllowedOriginPrincipals.Order(StringComparer.Ordinal));

            // Notifications consumes its own commands and must never be able to originate one; Auth, AuditLogs and
            // Tenant have no notification authority.
            Assert.DoesNotContain(TenantWrightMessagingTopology.NotificationsPrincipal, route.AllowedOriginPrincipals);
            Assert.DoesNotContain(TenantWrightMessagingTopology.AuthPrincipal, route.AllowedOriginPrincipals);
            Assert.DoesNotContain(TenantWrightMessagingTopology.AuditLogsPrincipal, route.AllowedOriginPrincipals);
            Assert.DoesNotContain(TenantWrightMessagingTopology.TenantPrincipal, route.AllowedOriginPrincipals);
        }

        Assert.Equal(
            TenantWrightMessagingTopology.NotificationTenantEndpoint,
            catalog.GetRoute(typeof(NotificationSendRequestedTenantEto)).CommandDestination);
        Assert.Equal(
            TenantWrightMessagingTopology.NotificationHostEndpoint,
            catalog.GetRoute(typeof(NotificationSendRequestedHostEto)).CommandDestination);
        Assert.Equal(
            TenantWrightMessagingTopology.NotificationsPrincipal,
            catalog.GetEndpointRoute(TenantWrightMessagingTopology.NotificationTenantEndpoint).OwnerPrincipal);
        Assert.Equal(
            TenantWrightMessagingTopology.NotificationsPrincipal,
            catalog.GetEndpointRoute(TenantWrightMessagingTopology.NotificationHostEndpoint).OwnerPrincipal);
    }

    [Fact]
    public void Shared_topology_should_register_only_shared_event_contracts()
    {
        var catalog = TenantWrightMessagingTopology.CreateCatalog();
        var sharedContractsAssembly = typeof(AuditLogWriteRequestedTenantEto).Assembly;

        // Service-owned business contracts stay in their own *.Domain.Shared assembly and are routed by the owning
        // host module. The shared catalog only learns principals, never service contracts.
        Assert.All(catalog.Routes, route => Assert.Same(sharedContractsAssembly, route.ContractType.Assembly));
        Assert.All(
            catalog.EndpointRoutes,
            endpoint => Assert.All(
                endpoint.ContractTypes,
                contract => Assert.Same(sharedContractsAssembly, contract.Assembly)));
    }

    [Fact]
    public void Service_owned_contract_routes_should_extend_the_catalog_without_a_shared_project_reference()
    {
        var serviceRoute = new TenantWrightContractRoute(
            typeof(TestServiceOwnedEventEto),
            TenantWrightMessageRouteKind.Event,
            null,
            new HashSet<string>(StringComparer.Ordinal) { TenantWrightMessagingTopology.IdentityPrincipal });
        var serviceEndpoint = new TenantWrightEndpointRoute(
            "tenantwright-permissions-service-owned-test-v1",
            TenantWrightMessagingTopology.PermissionsPrincipal,
            [typeof(TestServiceOwnedEventEto)]);

        var catalog = TenantWrightMessagingTopology.CreateCatalog([serviceRoute], [serviceEndpoint], []);

        var actualRoute = catalog.GetRoute(typeof(TestServiceOwnedEventEto));
        Assert.Equal(serviceRoute.ContractType, actualRoute.ContractType);
        Assert.Equal(serviceRoute.Kind, actualRoute.Kind);
        Assert.Equal(serviceRoute.AllowedOriginPrincipals, actualRoute.AllowedOriginPrincipals);

        var actualEndpoint = catalog.GetEndpointRoute(serviceEndpoint.Name);
        Assert.Equal(serviceEndpoint.Name, actualEndpoint.Name);
        Assert.Equal(serviceEndpoint.OwnerPrincipal, actualEndpoint.OwnerPrincipal);
        Assert.Equal(serviceEndpoint.ContractTypes, actualEndpoint.ContractTypes);
    }

    [Fact]
    public void Catalog_should_defensively_copy_route_and_endpoint_policy()
    {
        var origins = new HashSet<string>(StringComparer.Ordinal) { MessagingTestData.IdentityPrincipal };
        var contracts = new List<Type> { typeof(TenantDeactivatedEto) };
        var routes = new[]
        {
            new TenantWrightContractRoute(typeof(TenantDeactivatedEto), TenantWrightMessageRouteKind.Event, null, origins)
        };
        var endpointRoutes = new[]
        {
            new TenantWrightEndpointRoute(MessagingTestData.WorkflowsEndpoint, MessagingTestData.WorkflowsEndpoint, contracts)
        };

        var catalog = new TenantWrightMessageCatalog(routes, endpointRoutes, []);
        origins.Add("tenantwright-attacker");
        contracts.Add(typeof(NotificationSendRequestedTenantEto));

        var route = catalog.GetRoute(typeof(TenantDeactivatedEto));
        var endpoint = catalog.GetEndpointRoute(MessagingTestData.WorkflowsEndpoint);
        Assert.DoesNotContain("tenantwright-attacker", route.AllowedOriginPrincipals);
        Assert.Equal(new[] { typeof(TenantDeactivatedEto) }, endpoint.ContractTypes);
    }

    [Fact]
    public void Command_route_should_require_one_exact_declared_destination()
    {
        var routes = new[]
        {
            new TenantWrightContractRoute(
                typeof(NotificationSendRequestedTenantEto),
                TenantWrightMessageRouteKind.Command,
                MessagingTestData.NotificationsEndpoint,
                new HashSet<string>(StringComparer.Ordinal) { MessagingTestData.WorkflowsEndpoint })
        };
        var wrongEndpoint = new TenantWrightEndpointRoute(
            "tenantwright-wrong-destination",
            "tenantwright-wrong-destination",
            new[] { typeof(NotificationSendRequestedTenantEto) });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new TenantWrightMessageCatalog(routes, new[] { wrongEndpoint }, []));

        Assert.Contains("one declared endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_serialization_should_ignore_dictionary_insertion_order()
    {
        var first = CreateNotification(new Dictionary<string, string?>
        {
            ["zeta"] = "last",
            ["alpha"] = "first"
        });
        var second = CreateNotification(new Dictionary<string, string?>
        {
            ["alpha"] = "first",
            ["zeta"] = "last"
        });

        var firstBytes = TenantWrightContractSerializer.SerializeCanonical(first, typeof(NotificationSendRequestedTenantEto));
        var secondBytes = TenantWrightContractSerializer.SerializeCanonical(second, typeof(NotificationSendRequestedTenantEto));

        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(
            TenantWrightAttestationService.Sha256(firstBytes),
            TenantWrightAttestationService.Sha256(secondBytes));
    }

    [Fact]
    public void Canonical_payload_digest_should_change_when_sensitive_payload_changes()
    {
        var message = CreateNotification(new Dictionary<string, string?> { ["subject"] = "original" });
        var original = TenantWrightAttestationService.Sha256(
            TenantWrightContractSerializer.SerializeCanonical(message, typeof(NotificationSendRequestedTenantEto)));
        message.DeliveryPlan.Steps[0].TemplateModel["subject"] = "modified";

        var modified = TenantWrightAttestationService.Sha256(
            TenantWrightContractSerializer.SerializeCanonical(message, typeof(NotificationSendRequestedTenantEto)));

        Assert.NotEqual(original, modified);
    }

    [Fact]
    public void Outgoing_tenant_context_should_require_exact_body_and_ambient_tenant_match()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new MessagingTestCurrentTenant(tenantId);
        var message = new NotificationSendRequestedTenantEto
        {
            TenantId = tenantId,
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.Email
                    }]
            },
            Recipients = ((IEnumerable<NotificationRecipientEto>)[]).Select((recipient, index) => new NotificationRecipientPlan
            {
                Key = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreferenceUserId = null,
                Destinations = [new NotificationDestination
                {
                    Channel = NotificationSendChannel.Email,
                    RecipientType = recipient.RecipientType,
                    RecipientValue = recipient.RecipientValue
                }]
            }).ToList(),
        };

        var resolved = TenantWrightMessageContracts.ResolveTenant(
            MessagingTestData.Metadata<NotificationSendRequestedTenantEto>(), message, currentTenant);

        Assert.Equal(tenantId.ToString("D").ToLowerInvariant(), resolved);
        message.TenantId = Guid.NewGuid();
        var exception = Assert.Throws<MessagingSecurityException>(() =>
            TenantWrightMessageContracts.ResolveTenant(
                MessagingTestData.Metadata<NotificationSendRequestedTenantEto>(), message, currentTenant));
        Assert.Equal(MessagingSecurityException.TenantMismatch, exception.Message);
    }

    [Fact]
    public void Host_contract_should_reject_ambient_tenant_context()
    {
        var currentTenant = new MessagingTestCurrentTenant(Guid.NewGuid());
        var message = new NotificationSendRequestedHostEto();

        var exception = Assert.Throws<MessagingSecurityException>(() =>
            TenantWrightMessageContracts.ResolveTenant(
                MessagingTestData.Metadata<NotificationSendRequestedHostEto>(), message, currentTenant));

        Assert.Equal(MessagingSecurityException.TenantMismatch, exception.Message);
    }

    /// <summary>
    /// A tenant-scoped contract with no ambient tenant is refused rather than published without one.
    /// </summary>
    /// <remarks>
    /// The message body carrying a tenant identifier is not sufficient. Accepting it would let a background or
    /// unscoped path publish tenant data with no verifiable owner, which is the case a consumer cannot detect later.
    /// </remarks>
    [Fact]
    public void Tenant_contract_should_reject_a_missing_ambient_tenant()
    {
        var message = new NotificationSendRequestedTenantEto
        {
            TenantId = Guid.NewGuid(),
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.Email
                    }]
            },
            Recipients = ((IEnumerable<NotificationRecipientEto>)[]).Select((recipient, index) => new NotificationRecipientPlan
            {
                Key = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreferenceUserId = null,
                Destinations = [new NotificationDestination
                {
                    Channel = NotificationSendChannel.Email,
                    RecipientType = recipient.RecipientType,
                    RecipientValue = recipient.RecipientValue
                }]
            }).ToList(),
        };

        var exception = Assert.Throws<MessagingSecurityException>(() =>
            TenantWrightMessageContracts.ResolveTenant(
                MessagingTestData.Metadata<NotificationSendRequestedTenantEto>(),
                message,
                new MessagingTestCurrentTenant()));

        Assert.Equal(MessagingSecurityException.TenantMismatch, exception.Message);
    }

    /// <summary>A host-only contract published with no ambient tenant carries no tenant at all.</summary>
    [Fact]
    public void Host_contract_should_carry_no_tenant_when_none_is_ambient()
    {
        var resolved = TenantWrightMessageContracts.ResolveTenant(
            MessagingTestData.Metadata<NotificationSendRequestedHostEto>(),
            new NotificationSendRequestedHostEto(),
            new MessagingTestCurrentTenant());

        Assert.Null(resolved);
    }

    /// <summary>
    /// Pins the contract scopes the live RabbitMQ suite depends on.
    /// </summary>
    /// <remarks>
    /// The live suite published a host-only contract inside a tenant scope for a long time, and the mismatch was only
    /// visible in an opt-in Docker suite. Asserting the scopes here makes that class of drift fail in the fast suite.
    /// </remarks>
    [Fact]
    public void Live_suite_contract_scopes_should_stay_as_the_live_tests_assume()
    {
        Assert.Equal(
            Shared.Events.DistributedContractScope.TenantRequired,
            MessagingTestData.Metadata<PermissionGrantsChangedTenantEto>().Scope);
        Assert.Equal(
            Shared.Events.DistributedContractScope.HostOnly,
            MessagingTestData.Metadata<TenantDeactivatedEto>().Scope);
    }

    [Fact]
    public void Incoming_tenant_context_should_require_canonical_exact_tenant_binding()
    {
        var tenantId = Guid.NewGuid();
        var message = new NotificationSendRequestedTenantEto
        {
            TenantId = tenantId,
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.Email
                    }]
            },
            Recipients = ((IEnumerable<NotificationRecipientEto>)[]).Select((recipient, index) => new NotificationRecipientPlan
            {
                Key = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreferenceUserId = null,
                Destinations = [new NotificationDestination
                {
                    Channel = NotificationSendChannel.Email,
                    RecipientType = recipient.RecipientType,
                    RecipientValue = recipient.RecipientValue
                }]
            }).ToList(),
        };
        var origin = new OriginAttestation(
            Guid.NewGuid(),
            "Shared.EventContracts.NotificationSendRequestedTenantEto",
            MessagingTestData.IdentityPrincipal,
            "tenant",
            tenantId.ToString("D").ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            new string('a', 64),
            "key-1",
            "signature");

        var verified = TenantWrightMessageContracts.VerifyTenant(
            message, MessagingTestData.Metadata<NotificationSendRequestedTenantEto>(), origin);

        Assert.Equal(tenantId, verified);
        var mismatched = origin with { TenantId = Guid.NewGuid().ToString("D") };
        var exception = Assert.Throws<MessagingSecurityException>(() =>
            TenantWrightMessageContracts.VerifyTenant(
                message, MessagingTestData.Metadata<NotificationSendRequestedTenantEto>(), mismatched));
        Assert.Equal(MessagingSecurityException.TenantMismatch, exception.Message);
    }

    [Fact]
    public void Tenant_scope_should_restore_ambient_context_after_failure()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new MessagingTestCurrentTenant();

        Assert.Throws<ExpectedMessagingTestException>((Action)(() =>
        {
            using (currentTenant.Change(tenantId))
            {
                Assert.Equal(tenantId, currentTenant.Id);
                throw new ExpectedMessagingTestException();
            }
        }));

        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public async Task Send_filter_should_reject_unauthorized_command_origin_before_transport_access()
    {
        using var signer = new MessagingTestSigner(MessagingTestData.NotificationsPrincipal, "test-key-1");
        var filter = new TenantWrightSendTrustFilter<NotificationSendRequestedTenantEto>(
            MessagingTestData.CreateCatalog(),
            new TenantWrightAttestationService(signer, new MessagingTestPublicKeyRegistry(signer)),
            Microsoft.Extensions.Options.Options.Create(new MessagingOptions()),
            new FixedMessagingTimeProvider(DateTimeOffset.Parse("2026-07-25T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        var context = DispatchProxy.Create<SendContext<NotificationSendRequestedTenantEto>, MessageContextDispatchProxy<NotificationSendRequestedTenantEto>>();
        var contextProxy = (MessageContextDispatchProxy<NotificationSendRequestedTenantEto>)(object)context;
        contextProxy.Message = CreateNotification(new Dictionary<string, string?>());
        var next = DispatchProxy.Create<IPipe<SendContext<NotificationSendRequestedTenantEto>>, RecordingDispatchProxy>();

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() => filter.Send(context, next));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
        Assert.Empty(contextProxy.PayloadProbeTypes);
        Assert.Equal(0, contextProxy.UnexpectedInvocationCount);
        Assert.Equal(0, ((RecordingDispatchProxy)(object)next).InvocationCount);
    }

    [Fact]
    public async Task Publish_filter_should_reject_unauthorized_event_origin_before_transport_access()
    {
        using var signer = new MessagingTestSigner(MessagingTestData.NotificationsPrincipal, "test-key-1");
        var filter = new TenantWrightPublishTrustFilter<TenantDeactivatedEto>(
            MessagingTestData.CreateCatalog(),
            new TenantWrightAttestationService(signer, new MessagingTestPublicKeyRegistry(signer)));
        var context = DispatchProxy.Create<PublishContext<TenantDeactivatedEto>, MessageContextDispatchProxy<TenantDeactivatedEto>>();
        var contextProxy = (MessageContextDispatchProxy<TenantDeactivatedEto>)(object)context;
        contextProxy.Message = new TenantDeactivatedEto { TenantId = Guid.NewGuid() };
        var next = DispatchProxy.Create<IPipe<PublishContext<TenantDeactivatedEto>>, RecordingDispatchProxy>();

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() => filter.Send(context, next));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
        Assert.Empty(contextProxy.PayloadProbeTypes);
        Assert.Equal(0, contextProxy.UnexpectedInvocationCount);
        Assert.Equal(0, ((RecordingDispatchProxy)(object)next).InvocationCount);
    }

    private static NotificationSendRequestedTenantEto CreateNotification(
        Dictionary<string, string?> templateModel) => new()
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000111"),
            RequestId = Guid.Parse("00000000-0000-0000-0000-000000000222"),
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Steps = [new NotificationChannelPlan
                {
                    Channel = NotificationSendChannel.Email, TemplateName = "test-template", TemplateModel = templateModel
                }]
            },
            SourceService = "tenantwright-tests",
            OccurredAtUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc)
        };

    private class MessageContextDispatchProxy<TMessage> : DispatchProxy where TMessage : class
    {
        public TMessage Message { get; set; } = null!;
        public List<Type> PayloadProbeTypes { get; } = [];
        public int UnexpectedInvocationCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (string.Equals(targetMethod.Name, "get_Message", StringComparison.Ordinal))
                return Message;

            if (targetMethod.IsGenericMethod &&
                string.Equals(targetMethod.Name, "TryGetPayload", StringComparison.Ordinal))
            {
                PayloadProbeTypes.Add(targetMethod.GetGenericArguments().Single());
                args![0] = null;
                return false;
            }

            UnexpectedInvocationCount++;
            throw new InvalidOperationException($"Context member '{targetMethod.Name}' should not be called for an unauthorized origin.");
        }
    }

    private class RecordingDispatchProxy : DispatchProxy
    {
        public int InvocationCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            InvocationCount++;
            throw new InvalidOperationException("Transport should not be called for an unauthorized origin.");
        }
    }

    private sealed class ExpectedMessagingTestException : Exception;
}

[Shared.Events.EventName("Shared.EventBus.Tests.TestServiceOwnedEventEto")]
[Shared.Events.DistributedContract(1, Shared.Events.DistributedContractScope.TenantRequired, Shared.Events.DistributedContractDataClassification.Internal)]
public sealed class TestServiceOwnedEventEto
{
    public Guid TenantId { get; init; }
}
