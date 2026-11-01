using Medallion.Threading;
using Shared.DistributedLocking.Abstractions;

namespace Shared.DistributedLocking.Implementations;

internal sealed class MedallionDistributedLockHandle : IDistributedLockHandle
{
    private readonly IDistributedSynchronizationHandle _innerHandle;

    public string Name { get; }

    public MedallionDistributedLockHandle(string name, IDistributedSynchronizationHandle innerHandle)
    {
        Name = name;
        _innerHandle = innerHandle;
    }

    public ValueTask DisposeAsync()
    {
        return _innerHandle.DisposeAsync();
    }
}
