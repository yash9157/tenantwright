using Shared.Uow.Abstractions;

namespace Shared.Uow.Implementations;

public sealed class UnitOfWorkSuppressionContext : IUnitOfWorkSuppressionContext
{
    private int _depth;

    public bool IsSuppressed => Volatile.Read(ref _depth) > 0;

    public IDisposable Suppress()
    {
        Interlocked.Increment(ref _depth);
        return new Scope(this);
    }

    private sealed class Scope(UnitOfWorkSuppressionContext owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._depth);
            }
        }
    }
}
