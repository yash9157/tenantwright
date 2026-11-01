using Tenant.Domain.Shared.Enums;
using Shared.EventContracts;
using Shared.Events;
using Xunit;

namespace Tenant.Tests;

public sealed class TenantLifecycleDomainTests
{
    [Fact]
    public void Tenant_should_transition_from_provisioning_to_active()
    {
        var tenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "lifecycle-org");

        tenant.TransitionTo(TenantLifecycleStatus.Suspended);
        tenant.TransitionTo(TenantLifecycleStatus.Provisioning);
        tenant.TransitionTo(TenantLifecycleStatus.Active);

        Assert.Equal(TenantLifecycleStatus.Active, tenant.LifecycleStatus);
        Assert.True(tenant.IsActive);
        Assert.Equal(4, tenant.LifecycleRevision);
    }

    [Fact]
    public void Lifecycle_revision_should_advance_only_for_a_real_state_change()
    {
        var tenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "revision-org");

        tenant.TransitionTo(TenantLifecycleStatus.Active);
        Assert.Equal(1, tenant.LifecycleRevision);

        tenant.TransitionTo(TenantLifecycleStatus.Suspended);
        Assert.Equal(2, tenant.LifecycleRevision);
    }

    [Fact]
    public void Tenant_should_reject_transition_out_of_decommissioned()
    {
        var tenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "lifecycle-org");
        tenant.TransitionTo(TenantLifecycleStatus.Decommissioned);

        Assert.Throws<InvalidOperationException>(() => tenant.TransitionTo(TenantLifecycleStatus.Active));
        Assert.False(tenant.IsActive);
    }

    [Fact]
    public void Allowed_transitions_should_use_the_effective_legacy_status_without_mutating_the_aggregate()
    {
        var tenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "legacy-inactive");
        tenant.Deactivate();

        var allowed = tenant.GetAllowedTransitions();

        Assert.Contains(TenantLifecycleStatus.Active, allowed);
        Assert.Contains(TenantLifecycleStatus.Decommissioned, allowed);
        Assert.Equal(TenantLifecycleStatus.Suspended, tenant.LifecycleStatus);
    }

    [Fact]
    public void Lifecycle_event_should_keep_a_stable_wire_name()
    {
        var attribute = Assert.Single(typeof(TenantLifecycleChangedEto)
            .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
            .Cast<EventNameAttribute>());

        Assert.Equal("Shared.EventContracts.TenantLifecycleChangedEto", attribute.Name);
    }
}
