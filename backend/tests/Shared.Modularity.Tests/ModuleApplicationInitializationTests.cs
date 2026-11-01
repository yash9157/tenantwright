using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Modularity;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Xunit;

namespace Shared.Modularity.Tests;

public sealed class ModuleApplicationInitializationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Identity")]
    [InlineData("getdocument.insider")]
    [InlineData("GetDocument.Insider.Untrusted")]
    public void IsBuildTimeOpenApiGeneration_FailsClosed_ForOtherEntryAssemblies(string? entryAssemblyName)
    {
        Assert.False(Shared.Modularity.Extensions.ApplicationBuilderExtensions.IsBuildTimeOpenApiGeneration(entryAssemblyName));
    }

    [Fact]
    public void IsBuildTimeOpenApiGeneration_AcceptsMicrosoftExtractorEntryAssembly()
    {
        Assert.True(Shared.Modularity.Extensions.ApplicationBuilderExtensions.IsBuildTimeOpenApiGeneration("GetDocument.Insider"));
    }

    [Fact]
    public async Task InitializeAsync_StopsInvokingModules_WhenApplicationStoppingIsRequested()
    {
        LaterInitializationModule.WasInitialized = false;

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        using var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var application = new ModuleApplication(
            typeof(LaterInitializationModule),
            services,
            configuration);
        await application.ConfigureServicesAsync();

        await using var provider = services.BuildServiceProvider();
        await application.InitializeAsync(new ApplicationBuilder(provider));

        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(LaterInitializationModule.WasInitialized);
    }

    [Fact]
    public async Task InitializeAsync_HonorsRequestedExit_AfterInitializationPhaseCompletes()
    {
        ExitAfterInitializationModule.WasInitialized = false;
        ExitAfterInitializationModule.WasPostInitialized = false;

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        using var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var application = new ModuleApplication(
            typeof(ExitAfterInitializationModule),
            services,
            configuration);
        await application.ConfigureServicesAsync();

        await using var provider = services.BuildServiceProvider();
        await application.InitializeAsync(new ApplicationBuilder(provider));

        Assert.True(ExitAfterInitializationModule.WasInitialized);
        Assert.False(ExitAfterInitializationModule.WasPostInitialized);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [DependsOn(typeof(StopApplicationModule))]
    public sealed class LaterInitializationModule : BaseModule
    {
        public static bool WasInitialized { get; set; }

        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            WasInitialized = true;
        }
    }

    public sealed class StopApplicationModule : BaseModule
    {
        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            context.ServiceProvider
                .GetRequiredService<IHostApplicationLifetime>()
                .StopApplication();
        }
    }

    [DependsOn(typeof(RequestExitModule))]
    public sealed class ExitAfterInitializationModule : BaseModule
    {
        public static bool WasInitialized { get; set; }
        public static bool WasPostInitialized { get; set; }

        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            WasInitialized = true;
        }

        public override void OnPostApplicationInitialization(ApplicationInitializationContext context)
        {
            WasPostInitialized = true;
        }
    }

    public sealed class RequestExitModule : BaseModule
    {
        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            context.ServiceProvider
                .GetRequiredService<ApplicationInitializationExitSignal>()
                .RequestExit();
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            _stopping.Cancel();
        }

        public void Dispose()
        {
            _stopping.Dispose();
        }
    }
}
