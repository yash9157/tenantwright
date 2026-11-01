using Shared.Uow.Abstractions;

namespace AuditLogs.Tests.TestDoubles;

/// <summary>
/// Inline <see cref="IUnitOfWorkExecutor"/> test double that invokes each operation immediately and
/// records whether the call was transactional. It intentionally does not simulate persistence or
/// transaction ownership; relational behavior is covered with the real UoW/provider harness.
/// </summary>
internal sealed class RecordingUnitOfWorkExecutor : IUnitOfWorkExecutor
{
    /// <summary>Ordered record of each executed operation; <c>true</c> means transactional.</summary>
    public List<bool> Operations { get; } = [];

    public int TransactionalCount => Operations.Count(transactional => transactional);

    public int NonTransactionalCount => Operations.Count(transactional => !transactional);

    public async Task ExecuteTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operations.Add(true);
        await operation(cancellationToken);
    }

    public async Task<TResult> ExecuteTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operations.Add(true);
        return await operation(cancellationToken);
    }

    public async Task ExecuteNonTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operations.Add(false);
        await operation(cancellationToken);
    }

    public async Task<TResult> ExecuteNonTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operations.Add(false);
        return await operation(cancellationToken);
    }
}
