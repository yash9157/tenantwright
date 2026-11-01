using System.Security.Claims;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;

namespace Shared.Security;

[Dependency(ServiceLifetime.Singleton, TryRegister = true)]
public class ThreadCurrentPrincipalAccessor : CurrentPrincipalAccessorBase, ISingletonDependency
{
    protected override ClaimsPrincipal GetClaimsPrincipal()
    {
        return NormalizePrincipal(Thread.CurrentPrincipal as ClaimsPrincipal);
    }
}
