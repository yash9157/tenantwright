namespace Shared.Uow.Abstractions;

public interface IAmbientUnitOfWork
{
    IUnitOfWork? UnitOfWork { get; }

    void SetUnitOfWork(IUnitOfWork? unitOfWork);

    IUnitOfWork? GetCurrentByChecking();
}
