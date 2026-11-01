using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EventBus.Tests;

/// <summary>Only failures a handler explicitly marks transient get the providers' longer retry schedule.</summary>
public sealed class TenantWrightMessageFailureClassifierTests
{
    [Fact]
    public void A_marked_failure_is_transient_directly_or_wrapped()
    {
        Assert.True(TenantWrightMessageFailureClassifier.IsTransient(new ConvergingException()));
        Assert.True(TenantWrightMessageFailureClassifier.IsTransient(
            new InvalidOperationException("outer", new ConvergingException())));
        Assert.True(TenantWrightMessageFailureClassifier.IsTransient(
            new AggregateException(new InvalidOperationException("other"), new ConvergingException())));
    }

    [Fact]
    public void Unmarked_security_and_cancellation_failures_are_not_transient()
    {
        Assert.False(TenantWrightMessageFailureClassifier.IsTransient(new InvalidOperationException("permanent")));
        Assert.False(TenantWrightMessageFailureClassifier.IsTransient(
            new MessagingSecurityException(MessagingSecurityException.InvalidSignature)));
        Assert.False(TenantWrightMessageFailureClassifier.IsTransient(new OperationCanceledException()));
        Assert.False(TenantWrightMessageFailureClassifier.IsTransient(
            new TimeoutException("wrapped cancellation", new OperationCanceledException())));
    }

    [Fact]
    public void Classification_is_bounded_on_deep_chains()
    {
        Exception chain = new ConvergingException();
        for (var depth = 0; depth < 20; depth++)
        {
            chain = new InvalidOperationException("wrapper", chain);
        }

        Assert.False(TenantWrightMessageFailureClassifier.IsTransient(chain));
    }

    private sealed class ConvergingException()
        : InvalidOperationException("not converged yet"), ITenantWrightTransientMessageFailure;
}
