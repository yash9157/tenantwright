using Shared.Domain;
using Shared.EventContracts;
using Shared.Events;
using Xunit;

namespace Shared.EventContracts.Tests;

public sealed class ContractCatalogTests
{
    private static readonly string[] ExpectedContractNames =
    [
        nameof(AuditLogWriteRequestedHostEto),
        nameof(AuditLogWriteRequestedTenantEto),
        nameof(FeatureValuesChangedHostEto),
        nameof(FeatureValuesChangedTenantEto),
        nameof(IdentityPlatformRoleRetirementRequestedEto),
        nameof(IdentitySessionRevokedHostEto),
        nameof(IdentitySessionRevokedTenantEto),
        nameof(NotificationSendRequestedHostEto),
        nameof(NotificationSendRequestedTenantEto),
        nameof(PermissionGrantApprovalDecidedEto),
        nameof(PermissionGrantApprovalRequestedEto),
        nameof(PermissionGrantsChangedHostEto),
        nameof(PermissionGrantsChangedTenantEto),
        nameof(PermissionsPlatformRoleRetirementCompletedEto),
        nameof(TenantAdminGrantsProvisionedEto),
        nameof(TenantClosureChangedEto),
        nameof(TenantDatabaseChangedEto),
        nameof(TenantDeactivatedEto),
        nameof(TenantInitialOwnerOnboardingRequestedEto),
        nameof(TenantInitialOwnerOnboardingCompletedEto),
        nameof(TenantLifecycleChangedEto),
        nameof(TenantOrganizationProfileChangedEto),
        nameof(TenantOwnershipCancellationRequestedEto),
        nameof(TenantOwnershipCompletedEto),
        nameof(TenantOwnershipInvitationCanceledEto),
        nameof(TenantOwnershipInvitationCreatedEto),
        nameof(TenantOwnershipInvitationRequestedEto),
        nameof(TenantOwnershipInvitationVerifiedEto)
    ];

    [Fact]
    public void Concrete_contract_catalog_should_match_the_frozen_phase_one_catalog()
    {
        var actual = GetContracts()
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedContractNames.Order(StringComparer.Ordinal), actual, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_contract_should_have_canonical_identity_and_version_one_metadata()
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contractType in GetContracts())
        {
            var eventName = Assert.Single(
                contractType.GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
                    .Cast<EventNameAttribute>());
            var metadata = Assert.Single(
                contractType.GetCustomAttributes(typeof(DistributedContractAttribute), inherit: false)
                    .Cast<DistributedContractAttribute>());

            Assert.Equal($"Shared.EventContracts.{contractType.Name}", eventName.Name);
            Assert.Equal(1, metadata.SchemaVersion);
            Assert.True(identities.Add(eventName.Name), $"Duplicate contract identity '{eventName.Name}'.");
        }
    }

    [Fact]
    public void Scope_metadata_should_match_tenant_interface_semantics()
    {
        foreach (var contractType in GetContracts())
        {
            var metadata = contractType.GetCustomAttributes(typeof(DistributedContractAttribute), inherit: false)
                .Cast<DistributedContractAttribute>()
                .Single();
            var isMultiTenant = typeof(IMultiTenant).IsAssignableFrom(contractType);

            Assert.Equal(metadata.Scope == DistributedContractScope.TenantRequired, isMultiTenant);
        }
    }

    [Fact]
    public void Decorated_contracts_should_not_inherit_message_base_classes()
    {
        foreach (var contractType in GetContracts())
        {
            Assert.Equal(typeof(object), contractType.BaseType);
        }
    }

    [Fact]
    public void Explicit_host_split_contracts_should_not_serialize_a_tenant_identifier()
    {
        Type[] hostContracts =
        [
            typeof(AuditLogWriteRequestedHostEto),
            typeof(NotificationSendRequestedHostEto),
            typeof(IdentitySessionRevokedHostEto),
            typeof(FeatureValuesChangedHostEto),
            typeof(PermissionGrantsChangedHostEto)
        ];

        foreach (var contractType in hostContracts)
        {
            Assert.Null(contractType.GetProperty(nameof(IMultiTenant.TenantId)));
        }
    }

    private static Type[] GetContracts()
        => typeof(AuditLogWriteRequestedTenantEto).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.IsDefined(typeof(DistributedContractAttribute), inherit: false))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
}
