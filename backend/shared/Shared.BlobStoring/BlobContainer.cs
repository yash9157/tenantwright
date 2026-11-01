using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.MultiTenancy;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Shared.BlobStoring;

public sealed class BlobContainer<TContainer> : IBlobContainer<TContainer>
    where TContainer : class
{
    private readonly IBlobContainer _container;

    public BlobContainer(IBlobContainerFactory blobContainerFactory)
    {
        ArgumentNullException.ThrowIfNull(blobContainerFactory);
        _container = blobContainerFactory.Create<TContainer>();
    }

    internal BlobContainer(IBlobContainer container)
    {
        _container = container;
    }

    public Task SaveAsync(string name, Stream stream, bool overrideExisting = false, CancellationToken cancellationToken = default)
    {
        return _container.SaveAsync(name, stream, overrideExisting, cancellationToken);
    }

    public Task<Stream> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        return _container.GetAsync(name, cancellationToken);
    }

    public Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
    {
        return _container.GetOrNullAsync(name, cancellationToken);
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        return _container.ExistsAsync(name, cancellationToken);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        return _container.DeleteAsync(name, cancellationToken);
    }

    public Task<Uri> GetReadUrlAsync(string name, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        return _container.GetReadUrlAsync(name, duration, cancellationToken);
    }
}

public sealed class BlobContainer : IBlobContainer
{
    private readonly string _containerName;
    private readonly BlobContainerConfiguration _configuration;
    private readonly IBlobProvider _provider;
    private readonly ICurrentTenant _currentTenant;
    private readonly IOptionsMonitor<BlobStoringOptions> _optionsMonitor;
    private readonly IBlobNormalizeNamingService _blobNormalizeNamingService;
    private readonly ILogger<BlobContainer> _logger;

    public BlobContainer(
        string containerName,
        BlobContainerConfiguration configuration,
        IBlobProvider provider,
        ICurrentTenant currentTenant,
        IOptionsMonitor<BlobStoringOptions> optionsMonitor,
        IBlobNormalizeNamingService blobNormalizeNamingService,
        ILogger<BlobContainer> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(currentTenant);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(blobNormalizeNamingService);
        ArgumentNullException.ThrowIfNull(logger);

        _containerName = containerName;
        _configuration = configuration;
        _provider = provider;
        _currentTenant = currentTenant;
        _optionsMonitor = optionsMonitor;
        _blobNormalizeNamingService = blobNormalizeNamingService;
        _logger = logger;
    }

    public Task SaveAsync(string name, Stream stream, bool overrideExisting = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return ExecuteAsync(
            operation: "save",
            action: async () =>
            {
                var naming = NormalizeNames(name);
                await _provider.SaveAsync(
                    new BlobProviderSaveArgs(
                        naming.ContainerName!,
                        _configuration,
                        naming.BlobName!,
                        stream,
                        overrideExisting,
                        cancellationToken));
                return true;
            });
    }

    public async Task<Stream> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var stream = await GetOrNullAsync(name, cancellationToken);
        if (stream == null)
        {
            throw new BlobNotFoundException(
                $"Could not find blob '{name}' in container '{_containerName}'.");
        }

        return stream;
    }

    public Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            operation: "get",
            action: async () =>
            {
                var naming = NormalizeNames(name);
                return await _provider.GetOrNullAsync(
                    new BlobProviderGetArgs(
                        naming.ContainerName!,
                        _configuration,
                        naming.BlobName!,
                        cancellationToken));
            });
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            operation: "exists",
            action: async () =>
            {
                var naming = NormalizeNames(name);
                return await _provider.ExistsAsync(
                    new BlobProviderExistsArgs(
                        naming.ContainerName!,
                        _configuration,
                        naming.BlobName!,
                        cancellationToken));
            });
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            operation: "delete",
            action: async () =>
            {
                var naming = NormalizeNames(name);
                return await _provider.DeleteAsync(
                    new BlobProviderDeleteArgs(
                        naming.ContainerName!,
                        _configuration,
                        naming.BlobName!,
                        cancellationToken));
            });
    }

    public Task<Uri> GetReadUrlAsync(string name, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            operation: "get_read_url",
            action: async () =>
            {
                var naming = NormalizeNames(name);
                return await _provider.GetReadUrlAsync(
                    new BlobProviderReadUrlArgs(
                        naming.ContainerName!,
                        _configuration,
                        naming.BlobName!,
                        duration,
                        cancellationToken));
            });
    }

    private BlobNormalizeNaming NormalizeNames(string name)
    {
        var normalizedBlobName = BuildScopedBlobName(name);
        return _blobNormalizeNamingService.NormalizeNaming(
            _configuration,
            _containerName,
            normalizedBlobName);
    }

    private string BuildScopedBlobName(string blobName)
    {
        var normalizedName = BlobPathValidator.NormalizeRelativePath(blobName);

        if (_configuration.IsMultiTenant)
        {
            if (_currentTenant.Id.HasValue)
            {
                return $"tenants/{_currentTenant.Id.Value:D}/{normalizedName}";
            }

            if (_optionsMonitor.CurrentValue.FailWhenTenantMissingForMultiTenantContainers)
            {
                throw new BlobConfigurationException(
                    $"Container '{_containerName}' requires tenant context but no tenant is available.");
            }

            return $"host/{normalizedName}";
        }

        return $"shared/{normalizedName}";
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var providerName = _provider.GetType().Name;

        try
        {
            var result = await action();
            if (_optionsMonitor.CurrentValue.EnableTelemetry)
            {
                BlobStoringTelemetry.RecordSuccess(
                    operation,
                    providerName,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (_optionsMonitor.CurrentValue.EnableTelemetry)
            {
                BlobStoringTelemetry.RecordFailure(
                    operation,
                    providerName,
                    ex.GetType().Name,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            _logger.LogWarning(
                ex,
                "Blob operation failed. Operation={Operation}, Container={ContainerName}, Provider={Provider}.",
                operation,
                _containerName,
                providerName);

            throw;
        }
    }
}
