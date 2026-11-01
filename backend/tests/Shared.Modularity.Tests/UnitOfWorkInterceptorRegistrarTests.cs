using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.Uow.Implementations;
using Xunit;

namespace Shared.Modularity.Tests;

public class UnitOfWorkInterceptorRegistrarTests
{
    [Fact]
    public void RegisterIfNeeded_should_add_interceptor_for_unit_of_work_enabled_types()
    {
        DynamicProxyIgnoreTypes.Clear();
        var context = new OnServiceRegisteredContext(typeof(IMyService), typeof(MyService));

        UnitOfWorkInterceptorRegistrar.RegisterIfNeeded(context);

        Assert.Contains(typeof(UnitOfWorkInterceptor), context.Interceptors);
    }

    [Fact]
    public void RegisterIfNeeded_should_skip_ignored_types()
    {
        DynamicProxyIgnoreTypes.Clear();
        DynamicProxyIgnoreTypes.Add<MyService>();
        var context = new OnServiceRegisteredContext(typeof(IMyService), typeof(MyService));

        UnitOfWorkInterceptorRegistrar.RegisterIfNeeded(context);

        Assert.DoesNotContain(typeof(UnitOfWorkInterceptor), context.Interceptors);
    }

    public interface IMyService
    {
    }

    public sealed class MyService : IMyService, IUnitOfWorkEnabled
    {
    }
}
