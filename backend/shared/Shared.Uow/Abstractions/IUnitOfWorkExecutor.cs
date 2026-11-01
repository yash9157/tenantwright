namespace Shared.Uow.Abstractions;

/// <summary>
/// Executes a bounded operation inside a fresh automatic unit of work. Use this for orchestration
/// that requires multiple independent database phases and therefore cannot place one
/// <c>[UnitOfWork]</c> boundary on its outer method.
/// </summary>
public interface IUnitOfWorkExecutor
{
    Task ExecuteTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteNonTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteNonTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
