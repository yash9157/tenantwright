using Castle.DynamicProxy;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using System.Collections.Concurrent;
using System.Reflection;

namespace Shared.Uow.Implementations;

[ExposeServices(typeof(UnitOfWorkInterceptor))]
public class UnitOfWorkInterceptor : IInterceptor, ITransientDependency
{
    private readonly IUnitOfWorkManager _uowManager;
    private readonly IUnitOfWorkSuppressionContext _suppressionContext;
    private readonly IOptions<UnitOfWorkDefaultOptions> _defaultOptions;

    private static readonly MethodInfo InternalInterceptAsyncGenericMethodDefinition =
        typeof(UnitOfWorkInterceptor).GetMethod(nameof(InternalInterceptAsyncGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly ConcurrentDictionary<Type, MethodInfo> InternalInterceptAsyncGenericMethods = new();

    public UnitOfWorkInterceptor(
        IUnitOfWorkManager uowManager,
        IUnitOfWorkSuppressionContext suppressionContext,
        IOptions<UnitOfWorkDefaultOptions> defaultOptions)
    {
        _uowManager = uowManager;
        _suppressionContext = suppressionContext;
        _defaultOptions = defaultOptions;
    }

    public void Intercept(IInvocation invocation)
    {
        if (_suppressionContext.IsSuppressed)
        {
            invocation.Proceed();
            return;
        }

        var method = invocation.MethodInvocationTarget ?? invocation.Method;
        if (!UnitOfWorkHelper.IsUnitOfWorkMethod(method, out var unitOfWorkAttribute))
        {
            if (unitOfWorkAttribute?.IsDisabled == true)
            {
                invocation.Proceed();
                return;
            }

            if (!ReferenceEquals(method, invocation.Method) &&
                UnitOfWorkHelper.IsUnitOfWorkMethod(invocation.Method, out unitOfWorkAttribute))
            {
                method = invocation.Method;
            }
            else
            {
                invocation.Proceed();
                return;
            }
        }

        var options = UnitOfWorkHelper.CreateOptions(method, unitOfWorkAttribute, _defaultOptions.Value);
        var requiresNew = unitOfWorkAttribute?.RequiresNew == true;
        if (UnitOfWorkHelper.IsAsyncMethod(invocation.Method))
        {
            InterceptAsync(invocation, options, requiresNew);
            return;
        }

        throw new InvalidOperationException(
            $"Method '{method.DeclaringType?.FullName}.{method.Name}' uses [UnitOfWork] but does not return Task or Task<T>. " +
            "Synchronous unit-of-work interception is not supported.");
    }

    private void InterceptAsync(IInvocation invocation, UnitOfWorkOptions options, bool requiresNew)
    {
        var returnType = invocation.Method.ReturnType;

        if (returnType == typeof(Task))
        {
            invocation.ReturnValue = InternalInterceptAsync(invocation, options, requiresNew);
            return;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var closed = InternalInterceptAsyncGenericMethods.GetOrAdd(
                resultType,
                static t => InternalInterceptAsyncGenericMethodDefinition.MakeGenericMethod(t));

            invocation.ReturnValue = closed.Invoke(this, [invocation, options, requiresNew]);
            return;
        }

        invocation.Proceed();
    }

    private async Task InternalInterceptAsync(
        IInvocation invocation,
        UnitOfWorkOptions options,
        bool requiresNew)
    {
        if (!requiresNew && _uowManager.TryBeginReserved(UnitOfWork.UnitOfWorkReservationName, options))
        {
            invocation.Proceed();

            if (invocation.ReturnValue is not Task task)
            {
                throw new InvalidOperationException($"Expected return value of type Task for method {invocation.Method.Name}.");
            }

            await task;
            if (_uowManager.Current != null)
            {
                await _uowManager.Current.SaveChangesAsync();
            }

            return;
        }

        await using var uow = _uowManager.Begin(options, requiresNew);
        invocation.Proceed();

        if (invocation.ReturnValue is not Task proceedTask)
        {
            throw new InvalidOperationException($"Expected return value of type Task for method {invocation.Method.Name}.");
        }

        await proceedTask;
        await uow.CompleteAsync();
    }

    private async Task<T> InternalInterceptAsyncGeneric<T>(
        IInvocation invocation,
        UnitOfWorkOptions options,
        bool requiresNew)
    {
        if (!requiresNew && _uowManager.TryBeginReserved(UnitOfWork.UnitOfWorkReservationName, options))
        {
            invocation.Proceed();

            if (invocation.ReturnValue is not Task<T> task)
            {
                throw new InvalidOperationException($"Expected return value of type Task<{typeof(T).Name}> for method {invocation.Method.Name}.");
            }

            var result = await task;
            if (_uowManager.Current != null)
            {
                await _uowManager.Current.SaveChangesAsync();
            }

            return result;
        }

        await using var uow = _uowManager.Begin(options, requiresNew);
        invocation.Proceed();

        if (invocation.ReturnValue is not Task<T> proceedTask)
        {
            throw new InvalidOperationException($"Expected return value of type Task<{typeof(T).Name}> for method {invocation.Method.Name}.");
        }

        var response = await proceedTask;
        await uow.CompleteAsync();
        return response;
    }

}
