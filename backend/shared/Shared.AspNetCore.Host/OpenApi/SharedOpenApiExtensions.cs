using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace Shared.AspNetCore.Host.OpenApi;

public static class SharedOpenApiExtensions
{
    private const string DefaultRoutePattern = "/swagger/{documentName}/swagger.json";

    public static IServiceCollection AddSharedOpenApi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!ShouldEnableOpenApi(configuration, environment))
        {
            return services;
        }

        services.AddOpenApi(options =>
        {
            options.AddOperationTransformer<SharedProblemDetailsOperationTransformer>();
            options.AddOperationTransformer<SharedSortableFieldsOperationTransformer>();
        });
        return services;
    }

    public static WebApplication MapSharedOpenApi(this WebApplication app)
    {
        if (!ShouldEnableOpenApi(app.Configuration, app.Environment))
        {
            return app;
        }

        var routePattern = app.Configuration["OpenApi:RoutePattern"];
        if (string.IsNullOrWhiteSpace(routePattern))
        {
            routePattern = DefaultRoutePattern;
        }

        var openApiEndpoint = app.MapOpenApi(routePattern);
        if (app.Environment.IsDevelopment())
        {
            openApiEndpoint.AllowAnonymous();

            var scalarEndpoint = app.MapScalarApiReference("/scalar", options =>
            {
                options.WithOpenApiRoutePattern(routePattern);
            });
            scalarEndpoint.AllowAnonymous();

            // Redirect root / to /scalar in Development to avoid 401/404 errors when opening the service URL directly.
            // Exclude the Auth service since it serves its own UI at the root.
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
            if (!string.Equals(entryAssembly, "Auth", StringComparison.OrdinalIgnoreCase))
            {
                app.MapGet("/", context =>
                {
                    context.Response.Redirect("/scalar");
                    return System.Threading.Tasks.Task.CompletedTask;
                }).AllowAnonymous();
            }
        }
        else
        {
            openApiEndpoint.RequireAuthorization();
        }

        return app;
    }

    private static bool IsOpenApiEnabled(IConfiguration configuration)
    {
        return configuration.GetValue("OpenApi:Enabled", true);
    }

    private static bool ShouldEnableOpenApi(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!IsOpenApiEnabled(configuration))
        {
            return false;
        }

        var developmentOnly = configuration.GetValue("OpenApi:DevelopmentOnly", true);
        if (!developmentOnly)
        {
            return true;
        }

        return environment.IsDevelopment();
    }
}
