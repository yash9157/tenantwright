using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Autofac;

public sealed class SharedAutofacServiceScopeFactory : IServiceScopeFactory
{
    private readonly ILifetimeScope _lifetimeScope;

    public SharedAutofacServiceScopeFactory(ILifetimeScope lifetimeScope)
    {
        _lifetimeScope = lifetimeScope;
    }

    public IServiceScope CreateScope()
    {
        var childLifetimeScope = _lifetimeScope.BeginLifetimeScope();
        return new SharedAutofacServiceScope(childLifetimeScope);
    }

    private sealed class SharedAutofacServiceScope : IServiceScope, IAsyncDisposable
    {
        private readonly ILifetimeScope _lifetimeScope;

        public SharedAutofacServiceScope(ILifetimeScope lifetimeScope)
        {
            _lifetimeScope = lifetimeScope;
            ServiceProvider = new AutofacServiceProvider(_lifetimeScope);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
            _lifetimeScope.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            return _lifetimeScope.DisposeAsync();
        }
    }
}
