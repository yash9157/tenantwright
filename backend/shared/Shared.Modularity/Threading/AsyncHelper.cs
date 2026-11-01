using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Shared.Threading;

public static class AsyncHelper
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunSync<TResult>(Func<Task<TResult>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Task.Run(func).GetAwaiter().GetResult();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunSync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        Task.Run(func).GetAwaiter().GetResult();
    }
}
