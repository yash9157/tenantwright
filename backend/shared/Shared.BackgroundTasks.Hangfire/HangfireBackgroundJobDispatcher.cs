using Microsoft.Extensions.DependencyInjection;
using Shared.BackgroundTasks.Jobs;

namespace Shared.BackgroundTasks.Hangfire;

public sealed class HangfireBackgroundJobDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HangfireBackgroundJobDispatcher(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task DispatchAsync(BackgroundJobEnvelope envelope, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IBackgroundJobExecutor>();
        await executor.ExecuteAsync(envelope, scope.ServiceProvider, cancellationToken);
    }
}
