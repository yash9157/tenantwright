using Microsoft.AspNetCore.Mvc.Filters;

namespace Shared.Uow.AspNetCore;

internal sealed class UnitOfWorkPageFilter : IAsyncPageFilter
{
    private readonly UnitOfWorkRequestExecutor _executor;

    public UnitOfWorkPageFilter(UnitOfWorkRequestExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var methodInfo = context.HandlerMethod?.MethodInfo;
        if (methodInfo is null)
        {
            await next();
            return;
        }

        await _executor.ExecuteAsync(
            context.HttpContext,
            methodInfo,
            async () =>
            {
                var executedContext = await next();
                return new UnitOfWorkRequestExecution<PageHandlerExecutedContext>(
                    executedContext,
                    executedContext.Exception);
            });
    }
}
