using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Uow.Abstractions;
using Shared.Uow.Implementations;
using Shared.Uow.Options;

namespace Shared.Uow.AspNetCore;

internal readonly record struct UnitOfWorkRequestExecution<TContext>(
    TContext Context,
    Exception? Exception);

internal sealed class UnitOfWorkRequestExecutor
{
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly UnitOfWorkDefaultOptions _defaultOptions;
    private readonly IReadOnlyList<IPersistenceBoundaryInitializer> _persistenceInitializers;
    private readonly ILogger<UnitOfWorkRequestExecutor> _logger;

    public UnitOfWorkRequestExecutor(
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<UnitOfWorkDefaultOptions> defaultOptions,
        IEnumerable<IPersistenceBoundaryInitializer> persistenceInitializers,
        ILogger<UnitOfWorkRequestExecutor> logger)
    {
        _unitOfWorkManager = unitOfWorkManager ?? throw new ArgumentNullException(nameof(unitOfWorkManager));
        _defaultOptions = defaultOptions?.Value ?? throw new ArgumentNullException(nameof(defaultOptions));
        _persistenceInitializers = persistenceInitializers?.ToArray() ?? throw new ArgumentNullException(nameof(persistenceInitializers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TContext> ExecuteAsync<TContext>(
        HttpContext httpContext,
        MethodInfo methodInfo,
        Func<Task<UnitOfWorkRequestExecution<TContext>>> next)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(methodInfo);
        ArgumentNullException.ThrowIfNull(next);

        if (UnitOfWorkRequestState.WasMiddlewareSkipped(httpContext))
        {
            return (await next()).Context;
        }

        var policy = UnitOfWorkRequestPolicyResolver.Resolve(
            httpContext.Request,
            methodInfo,
            _defaultOptions);
        var requestState = UnitOfWorkRequestState.GetOrCreate(httpContext);
        if (!policy.IsEnabled)
        {
            var execution = await next();
            if (execution.Exception is not null)
            {
                requestState.MarkFailed();
            }

            return execution.Context;
        }

        if (!policy.RequiresNew &&
            _unitOfWorkManager.TryBeginReserved(UnitOfWork.UnitOfWorkReservationName, policy.Options))
        {
            var reservedUnitOfWork = _unitOfWorkManager.Current ??
                throw new InvalidOperationException(
                    "The reserved request unit of work was initialized but did not become ambient.");

            return await ExecuteParticipatingAsync(
                httpContext,
                reservedUnitOfWork,
                requestState,
                next);
        }

        var ambientUnitOfWork = _unitOfWorkManager.Current;
        if (!policy.RequiresNew && ambientUnitOfWork is not null)
        {
            EnsureCompatible(ambientUnitOfWork.Options, policy.Options, methodInfo);
            return await ExecuteParticipatingAsync(
                httpContext,
                ambientUnitOfWork,
                requestState,
                next);
        }

        if (!policy.RequiresNew)
        {
            _logger.LogWarning(
                "No reserved request unit of work was available for {Method}. Starting an independently owned fallback unit of work. Verify that UseUnitOfWork is registered after routing and before endpoint execution.",
                $"{methodInfo.DeclaringType?.FullName}.{methodInfo.Name}");
        }

        return await ExecuteOwnedAsync(
            httpContext,
            methodInfo,
            policy,
            requestState,
            next);
    }

    private async Task<TContext> ExecuteParticipatingAsync<TContext>(
        HttpContext httpContext,
        IUnitOfWork unitOfWork,
        UnitOfWorkRequestState requestState,
        Func<Task<UnitOfWorkRequestExecution<TContext>>> next)
    {
        try
        {
            await InitializePersistenceBoundariesAsync(httpContext.RequestAborted);
            var execution = await next();
            if (execution.Exception is not null)
            {
                requestState.MarkFailed();
                await TryRollbackAsync(unitOfWork, execution.Exception);
                return execution.Context;
            }

            await unitOfWork.SaveChangesAsync(httpContext.RequestAborted);
            return execution.Context;
        }
        catch (Exception exception)
        {
            requestState.MarkFailed();
            await TryRollbackAsync(unitOfWork, exception);
            throw;
        }
    }

    private async Task<TContext> ExecuteOwnedAsync<TContext>(
        HttpContext httpContext,
        MethodInfo methodInfo,
        UnitOfWorkRequestPolicy policy,
        UnitOfWorkRequestState requestState,
        Func<Task<UnitOfWorkRequestExecution<TContext>>> next)
    {
        var unitOfWork = _unitOfWorkManager.Begin(policy.Options, policy.RequiresNew);
        Exception? thrownException = null;
        Exception? actionException = null;

        try
        {
            await InitializePersistenceBoundariesAsync(httpContext.RequestAborted);
            var execution = await next();
            actionException = execution.Exception;
            if (actionException is not null)
            {
                requestState.MarkFailed();
                await TryRollbackAsync(unitOfWork, actionException);
                return execution.Context;
            }

            await unitOfWork.SaveChangesAsync(httpContext.RequestAborted);
            await unitOfWork.CompleteAsync(httpContext.RequestAborted);
            return execution.Context;
        }
        catch (Exception exception)
        {
            thrownException = exception;
            requestState.MarkFailed();
            await TryRollbackAsync(unitOfWork, exception);
            throw;
        }
        finally
        {
            try
            {
                await unitOfWork.DisposeAsync();
            }
            catch (Exception disposalException) when (thrownException is not null || actionException is not null)
            {
                _logger.LogError(
                    disposalException,
                    "Failed to dispose the independently owned unit of work for {Method} while preserving the original request failure.",
                    $"{methodInfo.DeclaringType?.FullName}.{methodInfo.Name}");
            }
        }
    }

    private async Task InitializePersistenceBoundariesAsync(CancellationToken cancellationToken)
    {
        foreach (var initializer in _persistenceInitializers)
        {
            await initializer.InitializeAsync(cancellationToken);
        }
    }

    private async Task TryRollbackAsync(IUnitOfWork unitOfWork, Exception originalException)
    {
        try
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            _logger.LogError(
                rollbackException,
                "Unit-of-work rollback failed while preserving the original request failure {FailureType}.",
                originalException.GetType().FullName);
        }
    }

    private static void EnsureCompatible(
        UnitOfWorkOptions ambientOptions,
        UnitOfWorkOptions requestOptions,
        MethodInfo methodInfo)
    {
        if (ambientOptions.IsTransactional == requestOptions.IsTransactional &&
            ambientOptions.IsolationLevel == requestOptions.IsolationLevel &&
            ambientOptions.Timeout == requestOptions.Timeout)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The ambient unit of work is not compatible with the request policy for '{methodInfo.DeclaringType?.FullName}.{methodInfo.Name}'. " +
            "Align the owning boundary options or use [UnitOfWork(RequiresNew = true)] for an intentional independent scope.");
    }
}
