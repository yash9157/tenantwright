using System.Data;

namespace Shared.Uow.Options;

public class UnitOfWorkOptions
{
    public bool IsTransactional { get; set; }

    public IsolationLevel? IsolationLevel { get; set; }

    public int? Timeout { get; set; }

    public UnitOfWorkOptions Clone()
    {
        return new UnitOfWorkOptions
        {
            IsTransactional = IsTransactional,
            IsolationLevel = IsolationLevel,
            Timeout = Timeout
        };
    }
}
