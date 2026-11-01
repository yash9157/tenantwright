using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.TenantPermissionAccess;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Attributes;
using Xunit;

namespace Permissions.Tests;

public class TenantPermissionAccessContractTests
{
    [Fact]
    public void Tenant_delegation_service_exposes_only_its_interceptable_interface()
    {
        var exposedServices = ExposedServiceExplorer.GetExposedServices(typeof(TenantDelegationAppService));

        Assert.Equal([typeof(ITenantDelegationAppService)], exposedServices);
        Assert.DoesNotContain(typeof(TenantDelegationAppService), exposedServices);
    }

    [Fact]
    public void Current_tenant_access_input_does_not_accept_scope_overrides()
    {
        var propertyNames = typeof(GetCurrentTenantPermissionAccessInput)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain("TenantId", propertyNames);
        Assert.DoesNotContain("UserId", propertyNames);
        Assert.DoesNotContain("RoleNames", propertyNames);
        Assert.DoesNotContain("ProviderName", propertyNames);
        Assert.DoesNotContain("ProviderKey", propertyNames);
    }

    [Fact]
    public void Tenant_access_service_exposes_current_tenant_read_only_contract()
    {
        var method = typeof(ITenantPermissionAccessAppService).GetMethod(nameof(ITenantPermissionAccessAppService.GetCurrentTenantAccessAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<TenantPermissionAccessDto>), method!.ReturnType);
    }

    [Fact]
    public void Tenant_delegation_mutation_is_tenant_implicit_revision_safe_and_revocable()
    {
        var propertyNames = typeof(TenantDelegationMutationInput)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain("TenantId", propertyNames);
        Assert.Contains(nameof(TenantDelegationMutationInput.ExpectedProviderVersion), propertyNames);
        Assert.Contains(nameof(TenantDelegationMutationInput.IdempotencyKey), propertyNames);
        Assert.Contains(nameof(TenantDelegationMutationInput.IsGranted), propertyNames);
    }

    [Fact]
    public void Tenant_delegation_query_returns_the_provider_revision_required_for_mutation()
    {
        Assert.NotNull(typeof(TenantDelegationEntryDto).GetProperty(nameof(TenantDelegationEntryDto.ProviderVersion)));
    }

    [Fact]
    public void Host_tenant_delegation_requires_explicit_tenant_and_operational_evidence()
    {
        var service = typeof(ITenantDelegationAppService);
        Assert.NotNull(service.GetMethod(nameof(ITenantDelegationAppService.GetHostCeilingAsync)));
        Assert.NotNull(service.GetMethod(nameof(ITenantDelegationAppService.UpdateHostCeilingAsync)));

        var input = typeof(UpdateHostTenantDelegationCeilingInput).GetProperties().Select(x => x.Name).ToArray();
        Assert.Contains(nameof(UpdateHostTenantDelegationCeilingInput.ExpectedProviderVersion), input);
        Assert.Contains(nameof(UpdateHostTenantDelegationCeilingInput.Changes), input);
        Assert.Contains(nameof(UpdateHostTenantDelegationCeilingInput.Reason), input);
        Assert.Contains(nameof(UpdateHostTenantDelegationCeilingInput.TicketId), input);
    }

    [Fact]
    public void Host_tenant_delegation_ceiling_update_is_transactional()
    {
        var method = typeof(TenantDelegationAppService).GetMethod(
            nameof(TenantDelegationAppService.UpdateHostCeilingAsync));

        var unitOfWork = Assert.Single(method!.GetCustomAttributes(typeof(UnitOfWorkAttribute), inherit: true));
        Assert.True(((UnitOfWorkAttribute)unitOfWork).IsTransactional);
    }

    [Fact]
    public void Tenant_delegation_options_expose_policy_constraints_without_provider_keys()
    {
        var properties = typeof(TenantDelegationOptionDto).GetProperties().Select(x => x.Name).ToArray();
        Assert.Contains(nameof(TenantDelegationOptionDto.PermissionName), properties);
        Assert.Contains(nameof(TenantDelegationOptionDto.AllowsUser), properties);
        Assert.Contains(nameof(TenantDelegationOptionDto.AllowsRole), properties);
        Assert.Contains(nameof(TenantDelegationOptionDto.RequiresApproval), properties);
        Assert.DoesNotContain("TenantId", properties);
        Assert.DoesNotContain("TargetId", properties);
    }
}
