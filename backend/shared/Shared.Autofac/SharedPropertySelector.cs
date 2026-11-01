using System.Reflection;
using Autofac.Core;
using Shared.Modularity.DependencyInjection;

namespace Shared.Autofac;

public class SharedPropertySelector : DefaultPropertySelector
{
    public SharedPropertySelector(bool preserveSetValues)
        : base(preserveSetValues)
    {
    }

    public override bool InjectProperty(PropertyInfo propertyInfo, object instance)
    {
        if (propertyInfo.GetCustomAttributes(typeof(DisablePropertyInjectionAttribute), true).Length != 0)
        {
            return false;
        }

        return base.InjectProperty(propertyInfo, instance);
    }
}
