using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Caching.Implementations;

internal static class SemaphoreSlimExtensions
{
    public static IDisposable Lock(this SemaphoreSlim semaphore)
    {
        semaphore.Wait();
        return new SemaphoreSlimLock(semaphore);
    }

    public static async Task<IDisposable> LockAsync(this SemaphoreSlim semaphore, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new SemaphoreSlimLock(semaphore);
    }

    private sealed class SemaphoreSlimLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public SemaphoreSlimLock(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}
