namespace Shared.Uow.Abstractions;

public interface ISupportsRollback
{
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
