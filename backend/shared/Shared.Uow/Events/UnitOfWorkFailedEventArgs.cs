using Shared.Uow.Abstractions;

namespace Shared.Uow.Events;

public class UnitOfWorkFailedEventArgs : UnitOfWorkEventArgs
{
    public Exception? Exception { get; }

    public bool IsRolledback { get; }

    public UnitOfWorkFailedEventArgs(IUnitOfWork unitOfWork, Exception? exception, bool isRolledback)
        : base(unitOfWork)
    {
        Exception = exception;
        IsRolledback = isRolledback;
    }
}
