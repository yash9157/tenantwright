
namespace Shared.Modularity;

public class DisposeAction : IDisposable
{
    private readonly Action _action;
    private int _disposed;

    public DisposeAction(Action action)
    {
        _action = action;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _action();
        GC.SuppressFinalize(this);
    }
}
