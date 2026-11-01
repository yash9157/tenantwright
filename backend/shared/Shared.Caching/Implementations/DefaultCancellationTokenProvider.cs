using System.Threading;
using Shared.Threading;

namespace Shared.Caching.Implementations;

public class DefaultCancellationTokenProvider : ICancellationTokenProvider
{
    public CancellationToken Token => CancellationToken.None;
}
