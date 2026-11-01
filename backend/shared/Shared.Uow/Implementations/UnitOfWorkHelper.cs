using System.Reflection;
using System.Threading.Tasks;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;
using Shared.Uow.Options;

namespace Shared.Uow.Implementations;

public static class UnitOfWorkHelper
{
    public static bool IsUnitOfWorkType(TypeInfo implementationType)
    {
        if (implementationType == null)
        {
            return false;
        }

        if (implementationType.IsDefined(typeof(UnitOfWorkAttribute), inherit: true))
        {
            return true;
        }

        if (implementationType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.IsDefined(typeof(UnitOfWorkAttribute), inherit: true)))
        {
            return true;
        }

        return typeof(IUnitOfWorkEnabled).GetTypeInfo().IsAssignableFrom(implementationType);
    }

    public static bool IsUnitOfWorkMethod(MethodInfo methodInfo, out UnitOfWorkAttribute? unitOfWorkAttribute)
    {
        ArgumentNullException.ThrowIfNull(methodInfo);

        unitOfWorkAttribute = GetUnitOfWorkAttributeOrNull(methodInfo);
        if (unitOfWorkAttribute != null)
        {
            return !unitOfWorkAttribute.IsDisabled;
        }

        return methodInfo.DeclaringType != null &&
               typeof(IUnitOfWorkEnabled).IsAssignableFrom(methodInfo.DeclaringType);
    }

    public static UnitOfWorkAttribute? GetUnitOfWorkAttributeOrNull(MethodInfo methodInfo)
    {
        ArgumentNullException.ThrowIfNull(methodInfo);

        return methodInfo.GetCustomAttribute<UnitOfWorkAttribute>(inherit: true) ??
               methodInfo.DeclaringType?.GetTypeInfo().GetCustomAttribute<UnitOfWorkAttribute>(inherit: true);
    }

    public static bool IsAsyncMethod(MethodInfo methodInfo)
    {
        return typeof(Task).IsAssignableFrom(methodInfo.ReturnType);
    }

    public static UnitOfWorkOptions CreateOptions(
        MethodInfo methodInfo,
        UnitOfWorkAttribute? attribute,
        UnitOfWorkDefaultOptions defaultOptions)
    {
        ArgumentNullException.ThrowIfNull(methodInfo);

        return CreateOptions(
            methodInfo,
            attribute,
            defaultOptions,
            autoValue: !methodInfo.Name.StartsWith("Get", StringComparison.InvariantCultureIgnoreCase));
    }

    public static UnitOfWorkOptions CreateOptions(
        MethodInfo methodInfo,
        UnitOfWorkAttribute? attribute,
        UnitOfWorkDefaultOptions defaultOptions,
        bool autoValue)
    {
        ArgumentNullException.ThrowIfNull(methodInfo);
        ArgumentNullException.ThrowIfNull(defaultOptions);

        var options = new UnitOfWorkOptions();
        if (attribute != null)
        {
            if (attribute.IsTransactional.HasValue)
            {
                options.IsTransactional = attribute.IsTransactional.Value;
            }

            options.IsolationLevel = attribute.IsolationLevel;
            options.Timeout = attribute.Timeout;
        }

        if (attribute?.IsTransactional == null)
        {
            options.IsTransactional = defaultOptions.CalculateIsTransactional(autoValue);
        }

        return defaultOptions.Normalize(options);
    }
}
