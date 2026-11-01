using Shared.Uow.Abstractions;

namespace Shared.Uow.Context;

public class AmbientUnitOfWork : IAmbientUnitOfWork
{
    private static readonly AsyncLocal<IUnitOfWork?> _currentUow = new();

    public IUnitOfWork? UnitOfWork => _currentUow.Value;

    public void SetUnitOfWork(IUnitOfWork? unitOfWork)
    {
        _currentUow.Value = unitOfWork;
    }

    public IUnitOfWork? GetCurrentByChecking()
    {
        var uow = UnitOfWork;
        while (uow != null && (uow.IsReserved || uow.IsDisposed || uow.IsCompleted))
        {
            uow = uow.Outer;
        }

        return uow;
    }
}
