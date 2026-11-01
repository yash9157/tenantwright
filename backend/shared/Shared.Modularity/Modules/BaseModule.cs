using System;
using System.Threading.Tasks;
using Shared.Modularity.Context;

namespace Shared.Modularity.Modules;

public abstract class BaseModule : ISharedModule
{
    protected internal ServiceConfigurationContext ServiceConfigurationContext
    {
        get
        {
            if (_serviceConfigurationContext == null)
            {
                throw new SharedException($"{nameof(ServiceConfigurationContext)} is only available in the {nameof(ConfigureServices)}, {nameof(PreConfigureServices)} and {nameof(PostConfigureServices)} methods.");
            }

            return _serviceConfigurationContext;
        }
        internal set => _serviceConfigurationContext = value;
    }

    private ServiceConfigurationContext? _serviceConfigurationContext;

    public virtual Task PreConfigureServicesAsync(ServiceConfigurationContext context)
    {
        ServiceConfigurationContext = context;
        PreConfigureServices(context);
        return Task.CompletedTask;
    }

    public virtual void PreConfigureServices(ServiceConfigurationContext context)
    {
    }

    public virtual Task ConfigureServicesAsync(ServiceConfigurationContext context)
    {
        ServiceConfigurationContext = context;
        ConfigureServices(context);
        return Task.CompletedTask;
    }

    public virtual void ConfigureServices(ServiceConfigurationContext context)
    {
    }

    public virtual Task PostConfigureServicesAsync(ServiceConfigurationContext context)
    {
        ServiceConfigurationContext = context;
        PostConfigureServices(context);
        return Task.CompletedTask;
    }

    public virtual void PostConfigureServices(ServiceConfigurationContext context)
    {
    }

    public virtual Task OnPreApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        OnPreApplicationInitialization(context);
        return Task.CompletedTask;
    }

    public virtual void OnPreApplicationInitialization(ApplicationInitializationContext context)
    {
    }

    public virtual Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        OnApplicationInitialization(context);
        return Task.CompletedTask;
    }

    public virtual void OnApplicationInitialization(ApplicationInitializationContext context)
    {
    }

    public virtual Task OnPostApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        OnPostApplicationInitialization(context);
        return Task.CompletedTask;
    }

    public virtual void OnPostApplicationInitialization(ApplicationInitializationContext context)
    {
    }

    public virtual Task OnApplicationShutdownAsync(ApplicationShutdownContext context)
    {
        OnApplicationShutdown(context);
        return Task.CompletedTask;
    }

    public virtual void OnApplicationShutdown(ApplicationShutdownContext context)
    {
    }
}
