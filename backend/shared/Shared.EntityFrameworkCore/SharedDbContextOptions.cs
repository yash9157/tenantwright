using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore.DependencyInjection;

namespace Shared.EntityFrameworkCore;

public class SharedDbContextOptions
{
    public IDictionary<Type, Type> ReplacedTypes { get; }

    public List<Action<SharedDbContextConfigurationContext>> DefaultPreConfigureActions { get; }

    public Action<SharedDbContextConfigurationContext>? DefaultConfigureAction { get; set; }

    public Dictionary<Type, List<object>> PreConfigureActions { get; }

    public Dictionary<Type, object> ConfigureActions { get; }

    public Action<DbContext, ModelConfigurationBuilder>? DefaultConventionAction { get; set; }

    public Dictionary<Type, List<object>> ConventionActions { get; }

    public Action<DbContext, ModelBuilder>? DefaultOnModelCreatingAction { get; set; }

    public Dictionary<Type, List<object>> OnModelCreatingActions { get; }

    public SharedDbContextOptions()
    {
        ReplacedTypes = new Dictionary<Type, Type>();
        DefaultPreConfigureActions = new List<Action<SharedDbContextConfigurationContext>>();
        PreConfigureActions = new Dictionary<Type, List<object>>();
        ConfigureActions = new Dictionary<Type, object>();
        ConventionActions = new Dictionary<Type, List<object>>();
        OnModelCreatingActions = new Dictionary<Type, List<object>>();
    }

    public virtual void PreConfigure(Action<SharedDbContextConfigurationContext> action)
    {
        DefaultPreConfigureActions.Add(action);
    }

    public virtual void PreConfigure<TDbContext>(Action<SharedDbContextConfigurationContext<TDbContext>> action)
        where TDbContext : SharedDbContext<TDbContext>
    {
        PreConfigureActions.GetOrAdd(typeof(TDbContext), () => new List<object>()).Add(action);
    }

    public virtual void Configure(Action<SharedDbContextConfigurationContext> action)
    {
        DefaultConfigureAction = action;
    }

    public virtual void Configure<TDbContext>(Action<SharedDbContextConfigurationContext<TDbContext>> action)
        where TDbContext : SharedDbContext<TDbContext>
    {
        ConfigureActions[typeof(TDbContext)] = action;
    }

    public virtual Type GetReplacedTypeOrSelf(Type dbContextType, MultiTenancySides side = MultiTenancySides.Both)
    {
        if (ReplacedTypes.TryGetValue(dbContextType, out var replacedType))
        {
            return replacedType;
        }

        return dbContextType;
    }
}