using System;
using System.Diagnostics.CodeAnalysis;


namespace Shared.EntityFrameworkCore.DependencyInjection;

public class SharedDbContextConfigurerAction : ISharedDbContextConfigurer
{

    public Action<SharedDbContextConfigurationContext> Action { get; }

    public SharedDbContextConfigurerAction(Action<SharedDbContextConfigurationContext> action)
    {
        // Check.NotNull(action, nameof(action));

        Action = action;
    }

    public void Configure(SharedDbContextConfigurationContext context)
    {
        Action.Invoke(context);
    }
}

public class SharedDbContextConfigurerAction<TDbContext> : SharedDbContextConfigurerAction
    where TDbContext : SharedDbContext<TDbContext>
{
    public SharedDbContextConfigurerAction(Action<SharedDbContextConfigurationContext> action)
        : base(action)
    {
    }
}
