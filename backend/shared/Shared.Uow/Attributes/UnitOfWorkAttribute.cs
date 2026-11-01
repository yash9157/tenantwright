using System.Data;

namespace Shared.Uow.Attributes;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Interface)]
public class UnitOfWorkAttribute : Attribute
{
    public bool? IsTransactional { get; set; }

    public IsolationLevel? IsolationLevel { get; set; }

    public int? Timeout { get; set; }

    public bool IsDisabled { get; set; }

    public bool RequiresNew { get; set; }

    public UnitOfWorkAttribute()
    {
    }

    public UnitOfWorkAttribute(bool isTransactional)
    {
        IsTransactional = isTransactional;
    }

    public UnitOfWorkAttribute(bool isTransactional, IsolationLevel isolationLevel)
    {
        IsTransactional = isTransactional;
        IsolationLevel = isolationLevel;
    }

    public UnitOfWorkAttribute(bool isTransactional, IsolationLevel isolationLevel, int timeout)
    {
        IsTransactional = isTransactional;
        IsolationLevel = isolationLevel;
        Timeout = timeout;
    }
}
