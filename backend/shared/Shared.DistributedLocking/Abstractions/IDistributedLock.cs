using Shared.DistributedLocking.Options;

namespace Shared.DistributedLocking.Abstractions;

public interface IDistributedLock
{
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string name,
        DistributedLockAcquireOptions options,
        CancellationToken cancellationToken = default);

    Task<IDistributedLockHandle> AcquireAsync(
        string name,
        DistributedLockAcquireOptions options,
        CancellationToken cancellationToken = default);
}
