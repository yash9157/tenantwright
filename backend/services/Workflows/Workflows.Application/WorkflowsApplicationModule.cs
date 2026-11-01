using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Workflows.Application.Contracts;
using Workflows.Application.Temporal;

namespace Workflows.Application;

[DependsOn(typeof(WorkflowsApplicationContractsModule))]
public class WorkflowsApplicationModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TemporalWorkflowOptions>, TemporalWorkflowOptionsValidator>());
        context.Services.AddOptions<TemporalWorkflowOptions>()
            .Bind(context.Configuration.GetSection(TemporalWorkflowOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        context.Services.TryAddSingleton<ITenantWrightTemporalClientProvider, TenantWrightTemporalClientProvider>();
        context.Services.TryAddSingleton<ITemporalWorkflowStarter, TemporalWorkflowStarter>();
        context.Services.TryAddSingleton<InitialOwnerOnboardingActivities>();
        context.Services.AddHostedService<TemporalWorkflowWorker>();
    }
}
