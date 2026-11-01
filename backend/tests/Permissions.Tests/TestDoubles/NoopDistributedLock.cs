using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;

namespace Permissions.Tests.TestDoubles;

internal sealed class NoopDistributedLock : IDistributedLock
{
    public Task<IDistributedLockHandle?> TryAcquireAsync(string name, DistributedLockAcquireOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult<IDistributedLockHandle?>(new NoopDistributedLockHandle(name));

    public Task<IDistributedLockHandle> AcquireAsync(string name, DistributedLockAcquireOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult<IDistributedLockHandle>(new NoopDistributedLockHandle(name));

    private sealed class NoopDistributedLockHandle : IDistributedLockHandle
    {
        public NoopDistributedLockHandle(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
