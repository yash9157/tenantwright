using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Tenants;
using Tenant.Controllers.Tenants;
using Xunit;
using TenantEntity = Tenant.Domain.Tenants.Tenant;

namespace Tenant.Tests;

/// <summary>
/// Tests for the Tenant gap-closure Phases P1–P3 (2026-07-12):
/// detail contract + concurrency stamp (P1), granular connection-string/restore
/// permissions + structured connection-string body (P2), and compliance/legal
/// profile fields (P3). Domain assertions run directly on the aggregate; contract
/// assertions lock the DTO/interface/controller surface via reflection.
/// </summary>
public sealed class TenantGapClosureP1P3Tests
{
    // ---- P3: Compliance / legal profile (domain) ----

    [Fact]
    public void Legal_and_data_governance_profiles_accept_valid_values()
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "Example Organization");
        tenant.SetLegalComplianceProfile("Example Organization Pvt Ltd", "REG-2001-012345", "29ABCDE1234F1Z5", "ISO 27001");
        tenant.SetDataGovernanceProfile("IN", 365);

        Assert.Equal("Example Organization Pvt Ltd", tenant.LegalEntityName);
        Assert.Equal("REG-2001-012345", tenant.RegistrationNumber);
        Assert.Equal("29ABCDE1234F1Z5", tenant.TaxId);
        Assert.Equal("ISO 27001", tenant.Accreditation);
        Assert.Equal("IN", tenant.DataRegion);
        Assert.Equal(365, tenant.RetentionPolicyDays);
    }

    [Theory]
    [InlineData("29ABCDE1234F1Z")]   // too short
    [InlineData("ABCDE1234F1Z50")]   // no leading state digits
    [InlineData("29abcde1234f1z5")]  // lowercase
    public void Compliance_profile_rejects_invalid_tax_id(string taxId)
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "Example Organization");
        Assert.Throws<ArgumentException>(() =>
            tenant.SetLegalComplianceProfile(null, null, taxId, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3651)]
    public void Compliance_profile_rejects_out_of_range_retention(int days)
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "Example Organization");
        Assert.Throws<ArgumentException>(() =>
            tenant.SetDataGovernanceProfile(null, days));
    }

    [Fact]
    public void Compliance_profile_normalizes_blank_values_to_null()
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "Example Organization");
        tenant.SetLegalComplianceProfile("   ", "  ", null, "");
        tenant.SetDataGovernanceProfile("  ", null);

        Assert.Null(tenant.LegalEntityName);
        Assert.Null(tenant.RegistrationNumber);
        Assert.Null(tenant.TaxId);
        Assert.Null(tenant.Accreditation);
        Assert.Null(tenant.DataRegion);
        Assert.Null(tenant.RetentionPolicyDays);
    }

    // ---- P1: Concurrency stamp (domain) ----

    [Fact]
    public void New_tenant_has_a_concurrency_stamp()
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "Example Organization");
        Assert.False(string.IsNullOrWhiteSpace(tenant.ConcurrencyStamp));
    }

    // ---- P1: Detail DTO shape (contract) ----

    [Fact]
    public void Detail_dto_exposes_timestamps_concurrency_and_database_status_but_no_raw_secret()
    {
        var props = typeof(TenantDetailDto).GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains("CreationTime", props);
        Assert.Contains("LastModificationTime", props);
        Assert.Contains("DeletionTime", props);
        Assert.Contains("ConcurrencyStamp", props);
        Assert.Contains("OwnerUserId", props);
        Assert.Contains("Databases", props);

        // The detail projection must never carry a raw connection-string value.
        Assert.DoesNotContain(props, p => p.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void List_dto_carries_creation_and_modification_timestamps()
    {
        var props = typeof(TenantDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.Contains("CreationTime", props);
        Assert.Contains("LastModificationTime", props);
    }

    [Fact]
    public void App_services_expose_detail_and_named_database_members()
    {
        var members = typeof(ITenantAppService).GetMethods().Select(m => m.Name).ToArray();
        Assert.Contains(nameof(ITenantAppService.GetDetailAsync), members);
        Assert.DoesNotContain(members, member => member.Contains("ConnectionString", StringComparison.Ordinal));

        var databaseMembers = typeof(ITenantDatabaseAppService).GetMethods().Select(m => m.Name).ToArray();
        Assert.Contains(nameof(ITenantDatabaseAppService.SetDatabaseAsync), databaseMembers);
        Assert.Contains(nameof(ITenantDatabaseAppService.RemoveDatabaseAsync), databaseMembers);
    }

    [Fact]
    public void Restore_takes_a_reason_input()
    {
        var method = typeof(ITenantAppService).GetMethod(nameof(ITenantAppService.RestoreAsync));
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(TenantRestoreInput), parameters[1].ParameterType);
    }

    [Fact]
    public void Detail_endpoint_is_a_get_returning_the_detail_dto()
    {
        var method = typeof(TenantsController).GetMethod(nameof(TenantsController.GetDetailAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<TenantDetailDto>), method!.ReturnType);
        var get = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Cast<HttpGetAttribute>().Single();
        Assert.Equal("{id:guid}/detail", get.Template);
    }

    // ---- P5: unified history + operational summary (contract) ----

    [Fact]
    public void History_endpoint_is_a_read_only_get_returning_the_paged_history()
    {
        var method = typeof(TenantsController).GetMethod(nameof(TenantsController.GetHistoryAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<PagedResultDto<TenantHistoryEntryDto>>), method!.ReturnType);
        var get = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Cast<HttpGetAttribute>().Single();
        Assert.Equal("{id:guid}/history", get.Template);
        // Reads are not audited and carry no method-level policy (class policy applies).
        Assert.DoesNotContain(method.GetCustomAttributes(inherit: true), a => a.GetType().Name == "AuditedAttribute");
        Assert.Empty(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    [Fact]
    public void Operational_summary_endpoint_is_a_read_only_get()
    {
        var method = typeof(TenantsController).GetMethod(nameof(TenantsController.GetOperationalSummaryAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<TenantOperationalSummaryDto>), method!.ReturnType);
        var get = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Cast<HttpGetAttribute>().Single();
        Assert.Equal("{id:guid}/operational-summary", get.Template);
        Assert.DoesNotContain(method.GetCustomAttributes(inherit: true), a => a.GetType().Name == "AuditedAttribute");
    }

    [Fact]
    public void History_entry_dto_carries_no_contact_pii()
    {
        var props = typeof(TenantHistoryEntryDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(props, p => p.Contains("Email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, p => p.Contains("Phone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Operational_summary_dto_exposes_dedicated_database_count_but_no_secret()
    {
        var props = typeof(TenantOperationalSummaryDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.Contains("DedicatedDatabaseCount", props);
        Assert.DoesNotContain(props, p => p.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, p => p.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void App_service_exposes_history_and_operational_summary_members()
    {
        var members = typeof(ITenantAppService).GetMethods().Select(m => m.Name).ToArray();
        Assert.Contains(nameof(ITenantAppService.GetHistoryAsync), members);
        Assert.Contains(nameof(ITenantAppService.GetOperationalSummaryAsync), members);
    }

    [Fact]
    public void Governance_contract_exposes_capabilities_impact_and_cursor_history_without_secrets()
    {
        var members = typeof(ITenantAppService).GetMethods().Select(m => m.Name).ToArray();
        Assert.Contains(nameof(ITenantAppService.GetActionImpactAsync), members);
        Assert.Contains(nameof(ITenantAppService.GetHistoryCursorAsync), members);
        Assert.Contains(nameof(ITenantAppService.RestoreDetailedAsync), members);
        Assert.Contains(nameof(ITenantAppService.RenameAsync), members);
        Assert.Contains(nameof(ITenantAppService.ResendOwnershipRequestAsync), members);

        var summaryProperties = typeof(TenantOperationalSummaryDto).GetProperties().Select(property => property.Name).ToArray();
        Assert.Contains("AllowedLifecycleTransitions", summaryProperties);
        Assert.Contains("CanArchive", summaryProperties);
        Assert.Contains("CanRestore", summaryProperties);

        var impactProperties = typeof(TenantActionImpactDto).GetProperties().Select(property => property.Name).ToArray();
        Assert.Contains("IdentityStatus", impactProperties);
        Assert.Contains("NotificationsStatus", impactProperties);
        Assert.DoesNotContain(impactProperties, property =>
            property.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Governance_controller_routes_are_read_only_or_exactly_permission_gated()
    {
        var impact = typeof(TenantsController).GetMethod(nameof(TenantsController.GetActionImpactAsync));
        Assert.NotNull(impact);
        Assert.Equal("{id:guid}/impact", impact!.GetCustomAttributes<HttpGetAttribute>(inherit: true).Single().Template);

        var rename = typeof(TenantsController).GetMethod(nameof(TenantsController.RenameAsync));
        Assert.NotNull(rename);
        Assert.Equal("{id:guid}/name", rename!.GetCustomAttributes<HttpPutAttribute>(inherit: true).Single().Template);
        Assert.Equal(TenantPermissions.Management.Update, rename.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Policy);

        var resend = typeof(TenantsController).GetMethod(nameof(TenantsController.ResendOwnershipRequestAsync));
        Assert.NotNull(resend);
        Assert.Equal("{id:guid}/ownership-requests/{requestId:guid}/resend", resend!.GetCustomAttributes<HttpPostAttribute>(inherit: true).Single().Template);
        Assert.Equal(TenantPermissions.Ownership.Transfer, resend.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Policy);
    }

    // ---- P2: Permission definitions (contract) ----

    [Fact]
    public void New_management_children_are_prefixed_by_the_parent()
    {
        Assert.StartsWith(TenantPermissions.Management.Default + ".", TenantPermissions.Management.Restore, StringComparison.Ordinal);
        Assert.StartsWith(TenantPermissions.Management.Default + ".", TenantPermissions.ConnectionStrings.Default, StringComparison.Ordinal);
        Assert.StartsWith(TenantPermissions.Management.Default + ".", TenantPermissions.Compliance.Default, StringComparison.Ordinal);
        Assert.StartsWith(TenantPermissions.Management.Default + ".", TenantPermissions.DataGovernance.Default, StringComparison.Ordinal);
    }

    [Fact]
    public void Organization_profile_contract_excludes_governed_fields()
    {
        var organizationProperties = typeof(TenantOrganizationProfileDto).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(nameof(TenantLegalComplianceProfileDto.LegalEntityName), organizationProperties);
        Assert.DoesNotContain(nameof(TenantLegalComplianceProfileDto.TaxId), organizationProperties);
        Assert.DoesNotContain(nameof(TenantDataGovernanceProfileDto.DataRegion), organizationProperties);
        Assert.DoesNotContain(nameof(TenantDataGovernanceProfileDto.RetentionPolicyDays), organizationProperties);
    }

    [Fact]
    public void Governed_profile_endpoints_use_distinct_exact_permissions()
    {
        AssertEndpointPolicy(
            nameof(TenantsController.GetLegalComplianceProfileAsync),
            TenantPermissions.Compliance.Read);
        AssertEndpointPolicy(
            nameof(TenantsController.UpdateLegalComplianceProfileAsync),
            TenantPermissions.Compliance.Update);
        AssertEndpointPolicy(
            nameof(TenantsController.GetDataGovernanceProfileAsync),
            TenantPermissions.DataGovernance.Read);
        AssertEndpointPolicy(
            nameof(TenantsController.UpdateDataGovernanceProfileAsync),
            TenantPermissions.DataGovernance.Update);
    }

    private static void AssertEndpointPolicy(string methodName, string expectedPolicy)
    {
        var method = typeof(TenantsController).GetMethod(methodName);
        Assert.NotNull(method);
        Assert.Equal(
            expectedPolicy,
            method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Policy);
    }

    [Fact]
    public void Connection_string_children_are_distinct_and_prefixed_by_their_family()
    {
        var children = new[]
        {
            TenantPermissions.ConnectionStrings.Read,
            TenantPermissions.ConnectionStrings.Update,
            TenantPermissions.ConnectionStrings.Delete,
            TenantPermissions.ConnectionStrings.Test,
        };

        Assert.Equal(children.Length, children.Distinct().Count());
        Assert.All(
            children,
            child => Assert.StartsWith(
                TenantPermissions.ConnectionStrings.Default + ".",
                child,
                StringComparison.Ordinal));
    }
}
