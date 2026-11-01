using System.Security.Claims;
using System.Threading;

namespace Shared.Security;

public abstract class CurrentPrincipalAccessorBase : ICurrentPrincipalAccessor
{
    private static readonly ClaimsPrincipal EmptyPrincipal = new(new ClaimsIdentity());

    private readonly AsyncLocal<ClaimsPrincipal?> _currentPrincipal = new();

    public ClaimsPrincipal Principal => _currentPrincipal.Value ?? GetClaimsPrincipal();

    public virtual IDisposable Change(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var parent = Principal;
        _currentPrincipal.Value = principal;
        return new RestorePrincipalScope(_currentPrincipal, parent);
    }

    protected abstract ClaimsPrincipal GetClaimsPrincipal();

    protected static ClaimsPrincipal NormalizePrincipal(ClaimsPrincipal? principal)
    {
        return principal ?? EmptyPrincipal;
    }

    private sealed class RestorePrincipalScope : IDisposable
    {
        private readonly AsyncLocal<ClaimsPrincipal?> _currentPrincipal;
        private readonly ClaimsPrincipal _parent;
        private bool _disposed;

        public RestorePrincipalScope(AsyncLocal<ClaimsPrincipal?> currentPrincipal, ClaimsPrincipal parent)
        {
            _currentPrincipal = currentPrincipal;
            _parent = parent;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _currentPrincipal.Value = _parent;
            _disposed = true;
        }
    }
}
