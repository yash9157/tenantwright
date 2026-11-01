using System.Threading;
using Shared.Modularity.DependencyInjection;

namespace Shared.MultiTenancy;

public class AsyncLocalTenantResolveResultAccessor : ITenantResolveResultAccessor, ISingletonDependency
{
    private readonly AsyncLocal<TenantResolveResultHolder> _holder = new();

    public TenantResolveResult? Result
    {
        get => _holder.Value?.Result;
        set
        {
            var holder = _holder.Value;
            if (holder != null)
            {
                holder.Result = null;
            }

            if (value != null)
            {
                _holder.Value = new TenantResolveResultHolder { Result = value };
            }
        }
    }

    private sealed class TenantResolveResultHolder
    {
        public TenantResolveResult? Result { get; set; }
    }
}
