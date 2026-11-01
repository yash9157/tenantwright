using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.BlobStoring.Providers.Aws;
using Shared.BlobStoring.Providers.Azure;
using Shared.BlobStoring.Providers.FileSystem;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;

namespace Shared.BlobStoring;

[DependsOn(typeof(MultiTenancyModule))]
public sealed class BlobStoringModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var isDevelopment = IsDevelopmentEnvironment();

        context.Services.AddOptions<BlobStoringOptions>()
            .Bind(context.Configuration.GetSection(BlobStoringOptions.SectionName));

        context.Services.PostConfigure<BlobStoringOptions>(options =>
        {
            var defaultConfiguration = options.Containers.GetConfiguration<DefaultContainer>();
            if (defaultConfiguration.ProviderType == null && isDevelopment)
            {
                defaultConfiguration.UseFileSystem(fileSystem =>
                {
                    if (string.IsNullOrWhiteSpace(fileSystem.BasePath))
                    {
                        fileSystem.BasePath = Path.Combine(AppContext.BaseDirectory, "blobs");
                    }
                });
            }
        });

        context.Services.TryAddTransient<IBlobContainerConfigurationProvider, DefaultBlobContainerConfigurationProvider>();
        context.Services.TryAddTransient<IBlobProviderSelector, DefaultBlobProviderSelector>();
        context.Services.TryAddTransient<IBlobNormalizeNamingService, BlobNormalizeNamingService>();
        context.Services.TryAddTransient<IBlobContainerFactory, BlobContainerFactory>();
        context.Services.TryAddTransient(typeof(IBlobContainer<>), typeof(BlobContainer<>));
        context.Services.TryAddTransient<IBlobContainer>(sp => sp.GetRequiredService<IBlobContainer<DefaultContainer>>());
        context.Services.TryAddSingleton<IBlobStoringConfigurationValidator, BlobStoringConfigurationValidator>();

        context.Services.TryAddSingleton<IAwsS3ClientFactory, AwsS3ClientFactory>();
        context.Services.TryAddSingleton<IAzureBlobServiceClientFactory, AzureBlobServiceClientFactory>();

        context.Services.TryAddTransient<FileSystemBlobProvider>();
        context.Services.TryAddTransient<AwsBlobProvider>();
        context.Services.TryAddTransient<AzureBlobProvider>();
        context.Services.TryAddTransient<FileSystemBlobNamingNormalizer>();
        context.Services.TryAddTransient<AwsBlobNamingNormalizer>();
        context.Services.TryAddTransient<AzureBlobNamingNormalizer>();

        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IBlobProvider, FileSystemBlobProvider>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IBlobProvider, AwsBlobProvider>());
        context.Services.TryAddEnumerable(ServiceDescriptor.Transient<IBlobProvider, AzureBlobProvider>());
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var isDevelopment = IsDevelopmentEnvironment();
        var validator = context.ServiceProvider.GetRequiredService<IBlobStoringConfigurationValidator>();
        validator.Validate(isDevelopment);
    }

    private static bool IsDevelopmentEnvironment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
