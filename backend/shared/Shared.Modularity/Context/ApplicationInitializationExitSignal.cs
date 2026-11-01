using System.Threading;

namespace Shared.Modularity.Context;

public sealed class ApplicationInitializationExitSignal
{
    private int _exitRequested;

    public bool IsExitRequested => Volatile.Read(ref _exitRequested) == 1;

    public void RequestExit()
    {
        Interlocked.Exchange(ref _exitRequested, 1);
    }
}
