namespace Shared.EventBus.Messaging;

/// <summary>
/// Marks a handler failure as transient: the message is valid and will succeed once another component converges
/// (a tenant cache that has not seen a committed change yet, a lock another replica holds). Provider adapters retry such
/// failures on a longer, bounded schedule before dead-lettering instead of spending their short immediate retries.
/// </summary>
/// <remarks>
/// Implement it only on exception types whose cause clears without operator action. Anything caller-correctable,
/// security-related or permanent must not carry it: the longer schedule only delays its dead-lettering.
/// </remarks>
public interface ITenantWrightTransientMessageFailure;

/// <summary>Classifies handler failures for provider retry policies.</summary>
public static class TenantWrightMessageFailureClassifier
{
    private const int MaximumDepth = 8;

    /// <summary>
    /// True when <paramref name="exception"/>, or an exception it wraps, implements
    /// <see cref="ITenantWrightTransientMessageFailure"/>. A security rejection or a cancellation is never transient.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsTransient(exception, depth: 0);
    }

    private static bool IsTransient(Exception? exception, int depth)
    {
        if (exception is null || depth > MaximumDepth ||
            exception is MessagingSecurityException or OperationCanceledException)
        {
            return false;
        }

        if (exception is ITenantWrightTransientMessageFailure)
        {
            return true;
        }

        return exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(inner => IsTransient(inner, depth + 1))
            : IsTransient(exception.InnerException, depth + 1);
    }
}
