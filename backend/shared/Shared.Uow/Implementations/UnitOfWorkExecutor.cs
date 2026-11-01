using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;

namespace Shared.Uow.Implementations;

[ExposeServices(typeof(IUnitOfWorkExecutor))]
public sealed class UnitOfWorkExecutor : IUnitOfWorkExecutor, ITransientDependency
{
    [UnitOfWork(isTransactional: true, RequiresNew = true)]
    public Task ExecuteTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(cancellationToken);
    }

    [UnitOfWork(isTransactional: true, RequiresNew = true)]
    public Task<TResult> ExecuteTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(cancellationToken);
    }

    [UnitOfWork(isTransactional: false, RequiresNew = true)]
    public Task ExecuteNonTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(cancellationToken);
    }

    [UnitOfWork(isTransactional: false, RequiresNew = true)]
    public Task<TResult> ExecuteNonTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(cancellationToken);
    }
}
