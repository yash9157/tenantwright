using System.Reflection;
using Microsoft.AspNetCore.Http;
using Shared.Uow.Attributes;
using Shared.Uow.Implementations;
using Shared.Uow.Options;

namespace Shared.Uow.AspNetCore;

internal readonly record struct UnitOfWorkRequestPolicy(
    bool IsEnabled,
    bool RequiresNew,
    UnitOfWorkOptions Options);

internal static class UnitOfWorkRequestPolicyResolver
{
    public static UnitOfWorkRequestPolicy Resolve(
        HttpRequest request,
        MethodInfo methodInfo,
        UnitOfWorkDefaultOptions defaultOptions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(methodInfo);
        ArgumentNullException.ThrowIfNull(defaultOptions);

        var attribute = UnitOfWorkHelper.GetUnitOfWorkAttributeOrNull(methodInfo);
        ValidateAttribute(methodInfo, attribute);

        if (attribute?.IsDisabled == true)
        {
            return new UnitOfWorkRequestPolicy(
                IsEnabled: false,
                RequiresNew: false,
                Options: new UnitOfWorkOptions());
        }

        var options = UnitOfWorkHelper.CreateOptions(
            methodInfo,
            attribute,
            defaultOptions,
            autoValue: IsTransactionalByDefault(request.Method));
        ValidateEffectiveOptions(methodInfo, attribute, options);

        return new UnitOfWorkRequestPolicy(
            IsEnabled: true,
            RequiresNew: attribute?.RequiresNew == true,
            Options: options);
    }

    private static bool IsTransactionalByDefault(string httpMethod)
    {
        return !HttpMethods.IsGet(httpMethod) &&
               !HttpMethods.IsHead(httpMethod) &&
               !HttpMethods.IsOptions(httpMethod) &&
               !HttpMethods.IsTrace(httpMethod);
    }

    private static void ValidateEffectiveOptions(
        MethodInfo methodInfo,
        UnitOfWorkAttribute? attribute,
        UnitOfWorkOptions options)
    {
        if (attribute?.IsolationLevel.HasValue != true || options.IsTransactional)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Unit-of-work metadata on '{methodInfo.DeclaringType?.FullName}.{methodInfo.Name}' is contradictory: an isolation level requires an effectively transactional unit of work.");
    }

    private static void ValidateAttribute(MethodInfo methodInfo, UnitOfWorkAttribute? attribute)
    {
        if (attribute is null)
        {
            return;
        }

        var methodName = $"{methodInfo.DeclaringType?.FullName}.{methodInfo.Name}";
        if (attribute.IsDisabled &&
            (attribute.IsTransactional.HasValue ||
             attribute.IsolationLevel.HasValue ||
             attribute.Timeout.HasValue ||
             attribute.RequiresNew))
        {
            throw new InvalidOperationException(
                $"Unit-of-work metadata on '{methodName}' is contradictory: a disabled unit of work cannot specify transaction, isolation, timeout, or RequiresNew options.");
        }
    }
}
