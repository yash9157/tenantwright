using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Modularity.Context;
using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;
using Shared.Modularity.Modules;
using Shared.Threading;
using System.Diagnostics;

namespace Shared.Modularity;

public interface IModuleApplication : IDisposable
{
    Type StartupModuleType { get; }
    IServiceCollection Services { get; }
    IConfiguration Configuration { get; }
    IReadOnlyList<ModuleDescriptor> Modules { get; }
    IServiceProvider ServiceProvider { get; }

    void ConfigureServices();
    Task ConfigureServicesAsync();
    void Initialize(IApplicationBuilder app);
    Task InitializeAsync(IApplicationBuilder app);
    void Shutdown();
    Task ShutdownAsync();
}

public partial class ModuleApplication : IModuleApplication
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Legacy DI alias registrations are enabled. Total aliases: {LegacyAliasCount}.")]
    private static partial void LogLegacyAliasesEnabled(ILogger logger, int legacyAliasCount);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Conventional DI registration summary: ScannedAssemblies={ScannedAssemblies}, ScannedTypes={ScannedTypes}, RegisteredDescriptors={RegisteredDescriptors}, DuplicateCollisionsBlocked={DuplicateCollisionsBlocked}, AmbiguousCollisionsBlocked={AmbiguousCollisionsBlocked}, RegistrationDurationMs={RegistrationDurationMs}, ExposureMode={ExposureMode}.")]
    private static partial void LogRegistrationSummary(
        ILogger logger,
        int scannedAssemblies,
        int scannedTypes,
        int registeredDescriptors,
        int duplicateCollisionsBlocked,
        int ambiguousCollisionsBlocked,
        long registrationDurationMs,
        ConventionalServiceExposureMode exposureMode);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Singleton normalization applied for {ServiceType}: ReplacedUnkeyedDescriptors={ReplacedUnkeyedDescriptors}, PreservedKeyedDescriptors={PreservedKeyedDescriptors}.")]
    private static partial void LogSingletonNormalization(
        ILogger logger,
        string serviceType,
        int replacedUnkeyedDescriptors,
        int preservedKeyedDescriptors);

    public Type StartupModuleType { get; }
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }
    public IServiceProvider ServiceProvider { get; private set; } = default!;

    public IReadOnlyList<ModuleDescriptor> Modules => _modules;

    private readonly List<ModuleDescriptor> _modules = new();

    public ModuleApplication(Type startupModuleType, IServiceCollection services, IConfiguration configuration)
    {
        StartupModuleType = startupModuleType;
        Services = services;
        Configuration = configuration;

        services.AddSingleton<IModuleApplication>(this);
        services.TryAddSingleton<ApplicationInitializationExitSignal>();
    }

    public void ConfigureServices()
    {
        AsyncHelper.RunSync(ConfigureServicesAsync);
    }

    public async Task ConfigureServicesAsync()
    {
        var moduleTypes = ModuleLoader.LoadModules(StartupModuleType);
        var registrationOptions =
            Services.GetSingletonInstanceOrNull<ConventionalRegistrationOptions>()
            ?? new ConventionalRegistrationOptions();
        ValidateRegistrationOptions(registrationOptions);

        _modules.Clear();
        foreach (var type in moduleTypes)
        {
            var instance = (ISharedModule)(Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Could not create module instance: {type.FullName}"));
            _modules.Add(new ModuleDescriptor(type, instance));
        }

        var moduleAssemblies = new ModuleAssemblyCollection(moduleTypes.Select(x => x.Assembly).Distinct());
        Services.Replace(ServiceDescriptor.Singleton(moduleAssemblies));

        if (Services.GetSingletonInstanceOrNull<ConventionalRegistrationReport>() == null)
        {
            Services.AddSingleton(new ConventionalRegistrationReport());
        }

        var context = new ServiceConfigurationContext(Services, Configuration);

        foreach (var module in _modules)
        {
            if (module.Instance is BaseModule baseModule)
            {
                baseModule.ServiceConfigurationContext = context;
            }
        }

        foreach (var module in _modules)
        {
            await module.Instance.PreConfigureServicesAsync(context);
        }

        var registrationStopwatch = Stopwatch.StartNew();
        var conventionalAssemblies = _modules
            .Select(x => x.Type.Assembly)
            .Distinct()
            .ToList();

        foreach (var assembly in conventionalAssemblies)
        {
            ConventionalRegistrar.RegisterAssembly(
                Services,
                assembly,
                registrationOptions);
        }
        registrationStopwatch.Stop();

        var diagnostics = Services.GetSingletonInstanceOrNull<ConventionalRegistrationDiagnostics>();
        diagnostics?.SetRegistrationDuration(registrationStopwatch.ElapsedMilliseconds);

        foreach (var module in _modules)
        {
            await module.Instance.ConfigureServicesAsync(context);
        }

        foreach (var module in _modules)
        {
            await module.Instance.PostConfigureServicesAsync(context);
        }
    }

    public void Initialize(IApplicationBuilder app)
    {
        AsyncHelper.RunSync(() => InitializeAsync(app));
    }

    public async Task InitializeAsync(IApplicationBuilder app)
    {
        ServiceProvider = app.ApplicationServices;
        var options = ServiceProvider.GetService<ConventionalRegistrationOptions>() ?? new ConventionalRegistrationOptions();
        var warnings = ServiceProvider.GetService<ConventionalRegistrationReport>();
        var diagnostics = ServiceProvider.GetService<ConventionalRegistrationDiagnostics>();
        var singletonNormalizationDiagnostics = ServiceProvider.GetService<SingletonNormalizationDiagnostics>();
        var logger = ServiceProvider.GetService<ILogger<ModuleApplication>>();
        if (warnings != null && warnings.LegacyAliases.Count > 0)
        {
            if (options.FailOnLegacyExposureMode)
            {
                throw new InvalidOperationException(
                    $"Legacy DI alias registrations are forbidden in strict mode. " +
                    $"Detected aliases: {warnings.LegacyAliases.Count}. " +
                    "Set Shared:DependencyInjection:ExposureMode=Default and remove legacy alias dependencies.");
            }

            if (logger != null)
            {
                LogLegacyAliasesEnabled(logger, warnings.LegacyAliases.Count);
            }
        }

        if (diagnostics != null && logger != null)
        {
            LogRegistrationSummary(
                logger,
                diagnostics.ScannedAssemblies,
                diagnostics.ScannedTypes,
                diagnostics.RegisteredDescriptors,
                diagnostics.DuplicateCollisionsBlocked,
                diagnostics.AmbiguousCollisionsBlocked,
                diagnostics.RegistrationDurationMs,
                options.ExposureMode);
        }

        if (singletonNormalizationDiagnostics != null && logger != null)
        {
            foreach (var record in singletonNormalizationDiagnostics.Records)
            {
                LogSingletonNormalization(
                    logger,
                    record.ServiceType,
                    record.ReplacedUnkeyedDescriptors,
                    record.PreservedKeyedDescriptors);
            }
        }

        var context = new ApplicationInitializationContext(ServiceProvider, app);

        foreach (var module in _modules)
        {
            await module.Instance.OnPreApplicationInitializationAsync(context);
            if (IsApplicationStopping())
            {
                return;
            }
        }

        foreach (var module in _modules)
        {
            await module.Instance.OnApplicationInitializationAsync(context);
            if (IsApplicationStopping())
            {
                return;
            }
        }

        if (ServiceProvider.GetRequiredService<ApplicationInitializationExitSignal>().IsExitRequested)
        {
            var lifetime = ServiceProvider.GetService<IHostApplicationLifetime>()
                ?? throw new InvalidOperationException(
                    "Application initialization requested process exit, but IHostApplicationLifetime is not registered.");
            lifetime.StopApplication();
            return;
        }

        foreach (var module in _modules)
        {
            await module.Instance.OnPostApplicationInitializationAsync(context);
            if (IsApplicationStopping())
            {
                return;
            }
        }
    }

    private bool IsApplicationStopping()
    {
        return ServiceProvider.GetService<IHostApplicationLifetime>()?
            .ApplicationStopping.IsCancellationRequested == true;
    }

    public void Shutdown()
    {
        AsyncHelper.RunSync(ShutdownAsync);
    }

    public async Task ShutdownAsync()
    {
        var context = new ApplicationShutdownContext(ServiceProvider);
        foreach (var module in _modules)
        {
            await module.Instance.OnApplicationShutdownAsync(context);
        }
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    private static void ValidateRegistrationOptions(ConventionalRegistrationOptions options)
    {
        if (options.FailOnLegacyExposureMode &&
            options.ExposureMode != ConventionalServiceExposureMode.Default)
        {
            throw new InvalidOperationException(
                $"Shared.Modularity strict mode requires ExposureMode='{ConventionalServiceExposureMode.Default}'. " +
                $"Current value is '{options.ExposureMode}'.");
        }
    }
}
