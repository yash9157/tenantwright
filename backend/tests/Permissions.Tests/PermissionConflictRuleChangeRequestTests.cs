using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Domain invariants for the four-eyes conflict-rule change workflow (BE-5). These guard the core
/// separation-of-duty safety property: the operator who requests a rule change can never be the one
/// who applies it, and decided requests are immutable.
/// </summary>
public class PermissionConflictRuleChangeRequestTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private static PermissionConflictRuleChangeRequest PendingCreate()
        => PermissionConflictRuleChangeRequest.ForCreate(
            Guid.NewGuid(), tenantId: null, "Payments.Approve", "Payments.Create",
            ruleReason: "SoD", requestReason: "finance control", ticketId: null,
            requestedByUserId: Requester, createdAtUtc: DateTime.UtcNow);

    [Fact]
    public void Requester_cannot_approve_their_own_request()
    {
        var request = PendingCreate();

        var error = Assert.Throws<InvalidOperationException>(
            () => request.Approve(Requester, decisionReason: null, DateTime.UtcNow));

        Assert.Contains("its own requester", error.Message, StringComparison.Ordinal);
        Assert.Equal(PermissionConflictRuleChangeStatus.Pending, request.Status);
    }

    [Fact]
    public void A_different_operator_can_approve()
    {
        var request = PendingCreate();

        request.Approve(Approver, "looks correct", DateTime.UtcNow);

        Assert.Equal(PermissionConflictRuleChangeStatus.Applied, request.Status);
        Assert.Equal(Approver, request.DecidedByUserId);
        Assert.Equal(2, request.Version);
    }

    [Fact]
    public void A_decided_request_cannot_be_decided_again()
    {
        var request = PendingCreate();
        request.Approve(Approver, null, DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(
            () => request.Reject(Approver, null, DateTime.UtcNow));
    }

    [Fact]
    public void Only_the_requester_can_cancel()
    {
        var request = PendingCreate();

        Assert.Throws<InvalidOperationException>(
            () => request.Cancel(Approver, reason: null, DateTime.UtcNow));

        request.Cancel(Requester, reason: "withdrawn", DateTime.UtcNow);
        Assert.Equal(PermissionConflictRuleChangeStatus.Cancelled, request.Status);
    }
}
