using Auth;
using Shared.Autofac;
using Shared.AspNetCore.Host.Logging;
using Shared.AspNetCore.Host.OpenApi;
using Shared.Modularity.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Host.UseSharedSerilog();
builder.Host.UseSharedAutofac();

builder.Services.AddApplication<AuthHostModule>(builder.Configuration);
builder.Services.AddSharedOpenApi(builder.Configuration, builder.Environment);

var app = builder.Build();

if (app.InitializeApplication())
{
    return;
}
app.MapSharedOpenApi();

app.Run();
