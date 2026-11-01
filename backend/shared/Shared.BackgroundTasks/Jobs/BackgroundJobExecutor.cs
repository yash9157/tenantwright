using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Shared.BackgroundTasks.Jobs;

public interface IBackgroundJobExecutor
{
    Task ExecuteAsync(
        BackgroundJobEnvelope envelope,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default);
}

public sealed class BackgroundJobExecutor : IBackgroundJobExecutor
{
    private readonly IBackgroundJobRegistry _registry;
    private readonly IBackgroundJobSerializer _serializer;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly UnitOfWorkDefaultOptions _unitOfWorkDefaults;

    public BackgroundJobExecutor(
        IBackgroundJobRegistry registry,
        IBackgroundJobSerializer serializer,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<UnitOfWorkDefaultOptions> unitOfWorkDefaults)
    {
        _registry = registry;
        _serializer = serializer;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _unitOfWorkDefaults = unitOfWorkDefaults.Value;
    }

    public async Task ExecuteAsync(
        BackgroundJobEnvelope envelope,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(envelope.JobName))
        {
            throw new InvalidOperationException("Background job envelope is missing a job name.");
        }

        var job = _registry.GetRequired(envelope.JobName);
        var args = _serializer.DeserializeArgs(envelope.ArgsJson, job.ArgsType)
                   ?? throw new InvalidOperationException($"Background job '{job.Name}' deserialized null args.");

        using var tenantScope = _currentTenant.Change(envelope.TenantId);
        var startedAt = BackgroundTasksTelemetry.StartTimestamp();
        var succeeded = false;
        try
        {
            await InvokeJobAsync(job, args, envelope, serviceProvider, cancellationToken);
            succeeded = true;
        }
        finally
        {
            BackgroundTasksTelemetry.RecordJobExecution(job.Name, startedAt, succeeded);
        }
    }

    private async Task InvokeJobAsync(
        BackgroundJobDefinition definition,
        object args,
        BackgroundJobEnvelope envelope,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var contextType = typeof(BackgroundJobExecutionContext<>).MakeGenericType(definition.ArgsType);
        var context = Activator.CreateInstance(
            contextType,
            args,
            envelope,
            serviceProvider,
            cancellationToken);

        var interfaceType = typeof(IBackgroundJob<>).MakeGenericType(definition.ArgsType);
        var interfaceMethod = interfaceType.GetMethod(nameof(IBackgroundJob<object>.ExecuteAsync))
                              ?? throw new InvalidOperationException($"Background job '{definition.Name}' has no ExecuteAsync method.");
        var interfaceMap = definition.JobType.GetInterfaceMap(interfaceType);
        var methodIndex = Array.IndexOf(interfaceMap.InterfaceMethods, interfaceMethod);
        if (methodIndex < 0)
        {
            throw new InvalidOperationException(
                $"Background job '{definition.Name}' does not map the registered ExecuteAsync method.");
        }

        var implementationMethod = interfaceMap.TargetMethods[methodIndex];

        if (!UnitOfWorkHelper.IsUnitOfWorkMethod(implementationMethod, out var unitOfWorkAttribute))
        {
            if (!UnitOfWorkHelper.IsUnitOfWorkMethod(interfaceMethod, out unitOfWorkAttribute))
            {
                await ResolveAndInvokeHandlerAsync(
                    interfaceMethod,
                    definition,
                    context,
                    serviceProvider);
                return;
            }
        }

        var options = UnitOfWorkHelper.CreateOptions(
            implementationMethod,
            unitOfWorkAttribute,
            _unitOfWorkDefaults);
        using var unitOfWork = _unitOfWorkManager.Begin(options, requiresNew: true);
        await ResolveAndInvokeHandlerAsync(
            interfaceMethod,
            definition,
            context,
            serviceProvider);
        await unitOfWork.CompleteAsync(cancellationToken);
    }

    private static Task ResolveAndInvokeHandlerAsync(
        MethodInfo method,
        BackgroundJobDefinition definition,
        object? context,
        IServiceProvider serviceProvider)
    {
        var handler = serviceProvider.GetRequiredService(definition.JobType);
        return InvokeHandlerAsync(method, handler, context, definition.Name);
    }

    private static async Task InvokeHandlerAsync(
        MethodInfo method,
        object handler,
        object? context,
        string jobName)
    {
        Task? task;
        try
        {
            task = (Task?)method.Invoke(handler, [context]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        if (task == null)
        {
            throw new InvalidOperationException($"Background job '{jobName}' returned null task.");
        }

        await task;
    }
}
