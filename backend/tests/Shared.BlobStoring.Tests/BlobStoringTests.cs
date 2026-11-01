using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.BlobStoring;
using Shared.BlobStoring.Providers.Aws;
using Shared.BlobStoring.Providers.Azure;
using Shared.BlobStoring.Providers.FileSystem;
using Shared.Modularity.Context;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.BlobStoring.Tests;

public class BlobStoringTests
{
    [Fact]
    public void Container_name_should_default_to_full_type_name_and_support_attribute_override()
    {
        var defaultName = BlobContainerNameAttribute.GetContainerName<ContainerWithoutAttribute>();
        var customName = BlobContainerNameAttribute.GetContainerName<ContainerWithAttribute>();

        Assert.Equal(typeof(ContainerWithoutAttribute).FullName, defaultName);
        Assert.Equal("custom-container", customName);
    }

    [Fact]
    public void Module_should_register_single_factory_and_default_container()
    {
        using var provider = BuildServiceProvider(
            environmentName: "Development",
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseFileSystem(fileSystem => fileSystem.BasePath = CreateTempDirectory());
                });
            });

        Assert.Single(provider.GetServices<IBlobContainerFactory>());
        Assert.NotNull(provider.GetRequiredService<IBlobContainer<DefaultContainer>>());
    }

    [Fact]
    public void Provider_selector_should_resolve_provider_by_container_configuration()
    {
        using var provider = BuildServiceProvider(
            environmentName: "Development",
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseFileSystem(fileSystem => fileSystem.BasePath = CreateTempDirectory());
                });

                options.Containers.Configure<ContainerWithAttribute>(container =>
                {
                    container.UseAws(aws =>
                    {
                        aws.Region = "us-east-1";
                        aws.AccessKeyId = "test-key";
                        aws.SecretAccessKey = "test-secret";
                    });
                });
            });

        var selector = provider.GetRequiredService<IBlobProviderSelector>();
        var providerInstance = selector.Get("custom-container");

        Assert.IsType<AwsBlobProvider>(providerInstance);
    }

    [Fact]
    public async Task File_system_provider_should_fail_closed_on_path_traversal_and_isolate_tenant_path()
    {
        var basePath = CreateTempDirectory();
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        using var provider = BuildServiceProvider(
            environmentName: "Development",
            tenantId: tenantId,
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseFileSystem(fileSystem =>
                    {
                        fileSystem.BasePath = basePath;
                        fileSystem.AppendContainerNameToBasePath = true;
                    });
                });
            });

        var container = provider.GetRequiredService<IBlobContainer<DefaultContainer>>();

        await Assert.ThrowsAsync<BlobConfigurationException>(() =>
            container.SaveAsync("../escape.txt", new MemoryStream([1, 2, 3])));

        await container.SaveAsync("reports/a.txt", new MemoryStream([1, 2, 3]));

        var expectedPath = Path.Combine(
            basePath,
            DefaultContainer.Name,
            "tenants",
            tenantId.ToString("D"),
            "reports",
            "a.txt");

        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task Save_without_override_should_throw_when_blob_exists()
    {
        var basePath = CreateTempDirectory();

        using var provider = BuildServiceProvider(
            environmentName: "Development",
            tenantId: Guid.NewGuid(),
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseFileSystem(fileSystem => fileSystem.BasePath = basePath);
                });
            });

        var container = provider.GetRequiredService<IBlobContainer<DefaultContainer>>();

        await container.SaveAsync("same.txt", new MemoryStream([5]));
        await Assert.ThrowsAsync<BlobAlreadyExistsException>(() =>
            container.SaveAsync("same.txt", new MemoryStream([7])));
    }

    [Fact]
    public async Task Get_should_throw_not_found_and_GetOrNull_should_return_null()
    {
        using var provider = BuildServiceProvider(
            environmentName: "Development",
            tenantId: Guid.NewGuid(),
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseFileSystem(fileSystem => fileSystem.BasePath = CreateTempDirectory());
                });
            });

        var container = provider.GetRequiredService<IBlobContainer<DefaultContainer>>();

        var missing = await container.GetOrNullAsync("missing.txt");
        Assert.Null(missing);

        await Assert.ThrowsAsync<BlobNotFoundException>(() => container.GetAsync("missing.txt"));
    }

    [Fact]
    public async Task Signed_url_support_should_match_provider_policy()
    {
        using var fsProvider = BuildServiceProvider(
            environmentName: "Development",
            tenantId: Guid.NewGuid(),
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseFileSystem(fileSystem => fileSystem.BasePath = CreateTempDirectory());
                });
            });

        var fsContainer = fsProvider.GetRequiredService<IBlobContainer<DefaultContainer>>();
        await Assert.ThrowsAsync<BlobProviderNotSupportedException>(() =>
            fsContainer.GetReadUrlAsync("x.txt", TimeSpan.FromMinutes(10)));

        using var awsProvider = BuildServiceProvider(
            environmentName: "Development",
            tenantId: Guid.NewGuid(),
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseAws(aws =>
                    {
                        aws.Region = "us-east-1";
                        aws.AccessKeyId = "test-key";
                        aws.SecretAccessKey = "test-secret";
                        aws.ContainerName = "test-bucket-blob";
                    });
                });
            });

        var awsContainer = awsProvider.GetRequiredService<IBlobContainer<DefaultContainer>>();
        var awsReadUrl = await awsContainer.GetReadUrlAsync("report.txt", TimeSpan.FromMinutes(15));
        var awsReadUrlText = awsReadUrl.ToString();
        Assert.True(
            awsReadUrlText.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase) ||
            awsReadUrlText.Contains("Signature=", StringComparison.OrdinalIgnoreCase),
            $"Unexpected AWS signed URL format: {awsReadUrlText}");

        using var azureProvider = BuildServiceProvider(
            environmentName: "Development",
            tenantId: Guid.NewGuid(),
            configureOptions: options =>
            {
                options.Containers.ConfigureDefault(container =>
                {
                    container.UseAzure(azure =>
                    {
                        azure.ConnectionString =
                            "DefaultEndpointsProtocol=https;AccountName=testaccount;" +
                            "AccountKey=AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=;" +
                            "EndpointSuffix=core.windows.net";
                        azure.ContainerName = "azure-test-container";
                    });
                });
            });

        var azureContainer = azureProvider.GetRequiredService<IBlobContainer<DefaultContainer>>();
        var azureReadUrl = await azureContainer.GetReadUrlAsync("report.txt", TimeSpan.FromMinutes(15));
        Assert.Contains("sig=", azureReadUrl.Query, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<BlobConfigurationException>(() =>
            azureContainer.GetReadUrlAsync("report.txt", TimeSpan.FromDays(8)));
    }

    [Fact]
    public void Production_environment_should_fail_closed_when_default_provider_missing()
    {
        var ex = Assert.Throws<BlobConfigurationException>(() =>
        {
            using var _ = BuildServiceProvider(
                environmentName: "Production",
                configureOptions: _ => { });
        });

        Assert.Contains("default container provider is not configured", ex.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildServiceProvider(
        string environmentName,
        Guid? tenantId = null,
        Action<BlobStoringOptions>? configureOptions = null)
    {
        var previousEnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environmentName);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentTenant>(new TestCurrentTenant(tenantId));

            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var context = new ServiceConfigurationContext(services, configuration);
            var module = new BlobStoringModule();
            module.ConfigureServices(context);

            if (configureOptions != null)
            {
                services.PostConfigure(configureOptions);
            }

            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IBlobStoringConfigurationValidator>().Validate(
                string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase));
            return provider;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironmentName);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "shared-blob-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        private Guid? _tenantId;

        public TestCurrentTenant(Guid? tenantId)
        {
            _tenantId = tenantId;
        }

        public bool IsAvailable => _tenantId.HasValue;

        public Guid? Id => _tenantId;

        public string? Name => _tenantId?.ToString("D");

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = _tenantId;
            _tenantId = id;
            return new Restore(() => _tenantId = previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _restore;

            public Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                _restore();
            }
        }
    }

    private sealed class ContainerWithoutAttribute
    {
    }

    [BlobContainerName("custom-container")]
    private sealed class ContainerWithAttribute
    {
    }
}
