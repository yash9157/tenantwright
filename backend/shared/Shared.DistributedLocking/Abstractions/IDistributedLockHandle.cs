namespace Shared.DistributedLocking.Abstractions;

public interface IDistributedLockHandle : IAsyncDisposable
{
    string Name { get; }
}
