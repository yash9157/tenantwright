using Tenant.Domain.Shared.Enums;
using Tenant.Domain.Tenants;
using Xunit;

namespace Tenant.Tests;

public sealed class TenantClosureDomainTests
{
    [Fact]
    public void Closure_request_should_close_and_restore_without_data_purge()
    {
        var request = new TenantClosureRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "tenant-closing", "Customer requested closure");
        request.Approve(Guid.NewGuid(), DateTime.UtcNow);
        request.Close(DateTime.UtcNow);
        request.Restore(DateTime.UtcNow);
        Assert.Equal(TenantClosureStatus.Restored, request.Status);
    }

    [Fact]
    public void Closure_request_can_only_be_canceled_while_requested()
    {
        var request = new TenantClosureRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "tenant-closing", null);
        request.Cancel();
        Assert.Equal(TenantClosureStatus.Canceled, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Approve(Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void Closure_request_rejection_is_terminal_and_records_decision()
    {
        var actorId = Guid.NewGuid();
        var rejectedAt = DateTime.UtcNow;
        var request = new TenantClosureRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "tenant-closing", null);

        request.Reject(actorId, rejectedAt, "insufficient-impact-review", " Keep active ");

        Assert.Equal(TenantClosureStatus.Rejected, request.Status);
        Assert.Equal(actorId, request.RejectedByUserId);
        Assert.Equal(rejectedAt, request.RejectedAtUtc);
        Assert.Equal("insufficient-impact-review", request.DecisionReasonCode);
        Assert.Equal("Keep active", request.DecisionNote);
        Assert.Throws<InvalidOperationException>(() => request.Approve(Guid.NewGuid(), DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => request.Reject(Guid.NewGuid(), DateTime.UtcNow, "again", null));
    }
}
