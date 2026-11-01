using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Shared.AspNetCore.Host.OpenApi;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public sealed class SharedOpenApiGenerationTests
{
    [Theory]
    [InlineData(OpenApiSpecVersion.OpenApi3_0)]
    [InlineData(OpenApiSpecVersion.OpenApi3_1)]
    public async Task Shared_generation_preserves_typed_success_and_default_problem_contracts(
        OpenApiSpecVersion version)
    {
        // Metadata only: this application is never started and invokes no handler or infrastructure.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(SharedOpenApiGenerationTests).Assembly.FullName
        });
        builder.Services.AddSharedOpenApi(builder.Configuration, builder.Environment);
        builder.Services.AddControllers().AddApplicationPart(typeof(SharedOpenApiContractProbeController).Assembly);
        builder.Services.Configure<OpenApiOptions>("v1", options =>
        {
            options.OpenApiVersion = version;
            options.ShouldInclude = description => description.ActionDescriptor is ControllerActionDescriptor action &&
                action.ControllerTypeInfo.AsType() == typeof(SharedOpenApiContractProbeController);
        });
        await using var application = builder.Build();
        application.MapControllers();
        var provider = application.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");

        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);
        var serialized = await document.SerializeAsJsonAsync(version, CancellationToken.None);
        using var json = JsonDocument.Parse(serialized);
        var operation = json.RootElement.GetProperty("paths").GetProperty("/contract-probe").GetProperty("get");
        var responses = operation.GetProperty("responses");

        Assert.True(responses.GetProperty("200").GetProperty("content").TryGetProperty("application/json", out _));
        var problem = responses.GetProperty("default").GetProperty("content").GetProperty("application/problem+json");
        Assert.True(problem.TryGetProperty("schema", out _));
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        var probe = schemas.GetProperty(nameof(OpenApiContractProbe)).GetProperty("properties");
        Assert.True(probe.TryGetProperty("state", out _));
        var problemShape = problem.GetProperty("schema");
        if (problemShape.TryGetProperty("$ref", out var reference))
        {
            const string componentPrefix = "#/components/schemas/";
            Assert.StartsWith(componentPrefix, reference.GetString(), StringComparison.Ordinal);
            problemShape = schemas.GetProperty(reference.GetString()![componentPrefix.Length..]);
        }
        var problemSchema = problemShape.GetProperty("properties");
        Assert.True(problemSchema.TryGetProperty("code", out _));
        Assert.True(problemSchema.TryGetProperty("traceId", out _));
    }

}

[ApiController]
[Route("contract-probe")]
public sealed class SharedOpenApiContractProbeController : ControllerBase
{
    [HttpGet]
    public OpenApiContractProbe Get() => new(HttpContext?.TraceIdentifier ?? "metadata-only");
}

public sealed record OpenApiContractProbe(string State);
