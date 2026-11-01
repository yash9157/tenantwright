using Shared.Uow.Abstractions;

namespace Shared.Uow.Events;

public class UnitOfWorkEventArgs : EventArgs
{
    public IUnitOfWork UnitOfWork { get; }

    public UnitOfWorkEventArgs(IUnitOfWork unitOfWork)
    {
        UnitOfWork = unitOfWork;
    }
}
