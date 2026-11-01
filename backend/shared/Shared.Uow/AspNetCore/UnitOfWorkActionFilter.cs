using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Shared.Uow.AspNetCore;

internal sealed class UnitOfWorkActionFilter : IAsyncActionFilter
{
    private readonly UnitOfWorkRequestExecutor _executor;

    public UnitOfWorkActionFilter(UnitOfWorkRequestExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.ActionDescriptor is not ControllerActionDescriptor controllerActionDescriptor)
        {
            await next();
            return;
        }

        await _executor.ExecuteAsync(
            context.HttpContext,
            controllerActionDescriptor.MethodInfo,
            async () =>
            {
                var executedContext = await next();
                return new UnitOfWorkRequestExecution<ActionExecutedContext>(
                    executedContext,
                    executedContext.Exception);
            });
    }
}
