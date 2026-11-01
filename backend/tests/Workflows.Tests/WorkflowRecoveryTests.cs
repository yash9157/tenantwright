using Microsoft.Extensions.Options;
using Moq;
using Shared.EventContracts;
using Shared.Events;
using Workflows.Application.Temporal;
using Xunit;

namespace Workflows.Tests;

public sealed class WorkflowRecoveryTests
{
    [Fact]
    public async Task Onboarding_terminal_signal_before_run_does_not_request_expiration()
    {
        var workflow = new InitialOwnerOnboardingWorkflow();
        await workflow.OnOwnershipCompletedAsync();
        await workflow.RunAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UnixEpoch));
        // Completion without a Temporal runtime proves no timer/activity was scheduled.
    }

    [Fact]
    public void Onboarding_replay_contract_has_no_invitation_payload()
    {
        var contract = typeof(TenantInitialOwnerOnboardingRequestedEto);
        Assert.Equal(new[] { "ExpiresAtUtc", "OccurredAtUtc", "RequestId", "TenantId" },
            contract.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        var metadata = Assert.Single(contract.GetCustomAttributes(typeof(DistributedContractAttribute), false)
            .Cast<DistributedContractAttribute>());
        Assert.Equal(DistributedContractScope.HostOnly, metadata.Scope);
        Assert.Equal(DistributedContractDataClassification.Internal, metadata.DataClassification);
    }

    [Fact]
    public async Task Invalid_onboarding_identity_is_rejected_before_client_acquisition()
    {
        var provider = new Mock<ITenantWrightTemporalClientProvider>(MockBehavior.Strict);
        var starter = new TemporalWorkflowStarter(provider.Object, Options.Create(new TemporalWorkflowOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => starter.StartInitialOwnerOnboardingAsync(new()
        {
            RequestId = Guid.CreateVersion7(),
            TenantId = Guid.Empty,
            OccurredAtUtc = DateTime.UnixEpoch,
            ExpiresAtUtc = DateTime.UnixEpoch.AddDays(7)
        }));
        provider.VerifyNoOtherCalls();
    }
}
