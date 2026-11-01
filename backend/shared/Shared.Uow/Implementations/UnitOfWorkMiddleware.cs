using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Uow.Abstractions;
using Shared.Uow.AspNetCore;
using Shared.Uow.Options;

namespace Shared.Uow.Implementations;

public class UnitOfWorkMiddleware
{
    private readonly RequestDelegate _next;
    private readonly UnitOfWorkMiddlewareOptions _options;
    private readonly ILogger<UnitOfWorkMiddleware> _logger;

    public UnitOfWorkMiddleware(
        RequestDelegate next,
        IOptions<UnitOfWorkMiddlewareOptions> options,
        ILogger<UnitOfWorkMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IUnitOfWorkManager uowManager)
    {
        if (ShouldSkip(context))
        {
            UnitOfWorkRequestState.GetOrCreate(context).MarkMiddlewareSkipped();
            await _next(context);
            return;
        }

        var unitOfWork = uowManager.Reserve(UnitOfWork.UnitOfWorkReservationName);
        Exception? requestException = null;
        var requestFailed = false;
        try
        {
            await _next(context);
            requestFailed = UnitOfWorkRequestState.HasRequestFailed(context);
            if (requestFailed)
            {
                await TryRollbackAsync(unitOfWork, "the MVC or Razor handler failed");
                return;
            }

            await unitOfWork.CompleteAsync(context.RequestAborted);
        }
        catch (Exception exception)
        {
            requestException = exception;
            await TryRollbackAsync(unitOfWork, "the request pipeline threw an exception");
            throw;
        }
        finally
        {
            try
            {
                await unitOfWork.DisposeAsync();
            }
            catch (Exception disposalException) when (requestException is not null || requestFailed)
            {
                _logger.LogError(
                    disposalException,
                    "Failed to dispose the request unit of work while preserving the original request failure {FailureType}.",
                    requestException?.GetType().FullName ?? "MVC or Razor handler failure");
            }
        }
    }

    private async Task TryRollbackAsync(IUnitOfWork unitOfWork, string reason)
    {
        try
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            _logger.LogError(
                rollbackException,
                "Request unit-of-work rollback failed after {Reason}.",
                reason);
        }
    }

    private bool ShouldSkip(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata?.GetMetadata<RootComponentMetadata>() != null)
        {
            return true;
        }

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        for (var i = 0; i < _options.IgnoredUrls.Count; i++)
        {
            if (path.StartsWith(_options.IgnoredUrls[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
