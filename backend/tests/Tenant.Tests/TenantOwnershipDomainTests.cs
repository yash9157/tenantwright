using Tenant.Domain.Shared.Enums;
using Tenant.Domain.Tenants;
using Xunit;
using TenantEntity = Tenant.Domain.Tenants.Tenant;

namespace Tenant.Tests;

public sealed class TenantOwnershipDomainTests
{
    [Fact]
    public void BeginProvisioning_should_make_new_tenant_inactive_until_owner_is_verified()
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "North Organization");

        tenant.BeginProvisioning();

        Assert.Equal(TenantLifecycleStatus.Provisioning, tenant.GetEffectiveLifecycleStatus());
        Assert.False(tenant.IsActive);
        Assert.Null(tenant.OwnerUserId);
    }

    [Fact]
    public void Ownership_request_should_not_complete_after_cancellation()
    {
        var request = new TenantOwnershipRequest(Guid.NewGuid(), Guid.NewGuid(), null, "owner@example.com", null,
            "initial-owner", null, TenantOwnershipRequestKind.InitialOwner);
        request.Cancel();

        Assert.False(request.Complete(Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Ownership_request_should_expire_when_its_deadline_passes()
    {
        var request = new TenantOwnershipRequest(Guid.NewGuid(), Guid.NewGuid(), null, "owner@example.com", null,
            "initial-owner", null, TenantOwnershipRequestKind.InitialOwner, DateTime.UtcNow.AddMinutes(-1));

        request.ExpireIfDue(DateTime.UtcNow);

        Assert.Equal(TenantOwnershipRequestStatus.Expired, request.Status);
    }

    [Fact]
    public void Ownership_request_resend_should_record_the_attempt_and_enforce_the_cooldown()
    {
        var request = new TenantOwnershipRequest(Guid.NewGuid(), Guid.NewGuid(), null, "owner@example.com", null,
            "transfer", null, TenantOwnershipRequestKind.Transfer);
        var now = DateTime.UtcNow;

        request.RecordResend(now);

        Assert.Equal(1, request.ResendCount);
        Assert.Equal(now, request.LastResentAtUtc);
        Assert.Throws<InvalidOperationException>(() => request.RecordResend(now.AddMinutes(4)));
        request.RecordResend(now.Add(TenantOwnershipRequest.MinimumResendInterval));
        Assert.Equal(2, request.ResendCount);
    }
}
