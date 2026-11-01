using System.Threading;

namespace Shared.Threading;

public interface ICancellationTokenProvider
{
    CancellationToken Token { get; }
}

public static class CancellationTokenProviderExtensions
{
    public static CancellationToken FallbackToProvider(this ICancellationTokenProvider provider, CancellationToken preToken)
    {
        return preToken == default ? provider.Token : preToken;
    }
}