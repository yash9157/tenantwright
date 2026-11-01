using System;
using System.Threading;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.Modularity;

namespace Shared.EntityFrameworkCore;

public class SharedEfCoreCurrentDbContext
{
    public ISharedEfCoreDbFunctionContext? Context => _currentContext.Value;

    private readonly AsyncLocal<ISharedEfCoreDbFunctionContext?> _currentContext = new();

    public IDisposable Use(ISharedEfCoreDbFunctionContext? context)
    {
        var previousContext = _currentContext.Value;
        _currentContext.Value = context;
        return new DisposeAction(() => _currentContext.Value = previousContext);
    }
}
