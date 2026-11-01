using Shared.Modularity.Extensions;
using Shared.Autofac;
using Shared.AspNetCore.Host.Logging;
using Shared.AspNetCore.Host.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Host.UseSharedSerilog();
builder.Host.UseSharedAutofac();

builder.Services.AddApplication<Notifications.NotificationsHostModule>(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddSharedOpenApi(builder.Configuration, builder.Environment);

var app = builder.Build();

if (app.InitializeApplication())
{
    return;
}
app.MapSharedOpenApi();

app.Run();
