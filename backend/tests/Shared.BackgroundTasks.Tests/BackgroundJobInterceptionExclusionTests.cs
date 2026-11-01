using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.BackgroundTasks.Jobs;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Attributes;
using Shared.Uow.Implementations;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

/// <summary>
/// Regression guard: annotating a durable job's <c>ExecuteAsync</c> with <c>[UnitOfWork]</c> must not
/// cause the Autofac conventional registrar to class-proxy the (internal, sealed) job type. The
/// executor reads the attribute and opens the unit of work itself, so <see cref="AddBackgroundJob"/>
/// registers the job type in <see cref="DynamicProxyIgnoreTypes"/> to keep it out of interception.
/// Without this, internal/sealed annotated jobs crash the host at container build.
/// </summary>
public class BackgroundJobInterceptionExclusionTests
{
    [Fact]
    public void AddBackgroundJob_excludes_annotated_job_from_dynamic_proxy_interception()
    {
        // The job carries [UnitOfWork], so without the exclusion the interceptor registrar would try to
        // proxy it - this proves the exclusion is doing real work, not masking a no-op.
        Assert.True(UnitOfWorkHelper.IsUnitOfWorkType(typeof(AnnotatedSealedJob).GetTypeInfo()));

        var services = new ServiceCollection();
        services.AddBackgroundJob<AnnotatedSealedJob, AnnotatedArgs>("annotated-sealed-job");

        Assert.True(DynamicProxyIgnoreTypes.Contains(typeof(AnnotatedSealedJob)));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AnnotatedSealedJob));
    }

    private sealed record AnnotatedArgs;

    private sealed class AnnotatedSealedJob : IBackgroundJob<AnnotatedArgs>
    {
        [UnitOfWork(isTransactional: false)]
        public Task ExecuteAsync(BackgroundJobExecutionContext<AnnotatedArgs> context)
            => Task.CompletedTask;
    }
}
