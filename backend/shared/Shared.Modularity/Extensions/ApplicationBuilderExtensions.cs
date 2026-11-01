using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Shared.Modularity.Extensions;

public static class ApplicationBuilderExtensions
{
    private const string OpenApiDocumentExtractorAssemblyName = "GetDocument.Insider";

    public static bool InitializeApplication(this IApplicationBuilder app)
    {
        if (IsBuildTimeOpenApiGeneration(Assembly.GetEntryAssembly()?.GetName().Name))
        {
            return true;
        }

        var application = app.ApplicationServices.GetRequiredService<ModuleApplication>();
        application.Initialize(app);
        return app.ApplicationServices
            .GetRequiredService<ApplicationInitializationExitSignal>()
            .IsExitRequested;
    }

    internal static bool IsBuildTimeOpenApiGeneration(string? entryAssemblyName)
    {
        return string.Equals(
            entryAssemblyName,
            OpenApiDocumentExtractorAssemblyName,
            StringComparison.Ordinal);
    }
}
