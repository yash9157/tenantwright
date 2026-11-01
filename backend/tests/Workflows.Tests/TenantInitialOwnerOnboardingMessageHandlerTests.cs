using Moq;
using Shared.EventContracts;
using Workflows.Application.Temporal;
using Xunit;

namespace Workflows.Tests;

public sealed class TenantInitialOwnerOnboardingMessageHandlerTests
{
    [Fact]
    public async Task Requested_command_should_start_the_onboarding_workflow_with_the_consume_token()
    {
        var command = new TenantInitialOwnerOnboardingRequestedEto();
        using var cancellation = new CancellationTokenSource();
        var workflows = new Mock<ITemporalWorkflowStarter>(MockBehavior.Strict);
        workflows.Setup(starter => starter.StartInitialOwnerOnboardingAsync(command, cancellation.Token))
            .Returns(Task.CompletedTask);
        var handler = new TenantInitialOwnerOnboardingMessageHandler(workflows.Object);

        await handler.HandleAsync(command, cancellation.Token);

        workflows.Verify(starter => starter.StartInitialOwnerOnboardingAsync(command, cancellation.Token), Times.Once);
        workflows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Completed_command_should_complete_the_onboarding_workflow_with_the_consume_token()
    {
        var command = new TenantInitialOwnerOnboardingCompletedEto();
        using var cancellation = new CancellationTokenSource();
        var workflows = new Mock<ITemporalWorkflowStarter>(MockBehavior.Strict);
        workflows.Setup(starter => starter.CompleteInitialOwnerOnboardingAsync(command, cancellation.Token))
            .Returns(Task.CompletedTask);
        var handler = new TenantInitialOwnerOnboardingMessageHandler(workflows.Object);

        await handler.HandleAsync(command, cancellation.Token);

        workflows.Verify(starter => starter.CompleteInitialOwnerOnboardingAsync(command, cancellation.Token), Times.Once);
        workflows.VerifyNoOtherCalls();
    }
}
