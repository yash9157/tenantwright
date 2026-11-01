using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.MultiTenancy;

namespace Shared.BlobStoring;

public sealed class BlobContainerFactory : IBlobContainerFactory
{
    private readonly IBlobContainerConfigurationProvider _configurationProvider;
    private readonly IBlobProviderSelector _providerSelector;
    private readonly ICurrentTenant _currentTenant;
    private readonly IOptionsMonitor<BlobStoringOptions> _optionsMonitor;
    private readonly IBlobNormalizeNamingService _blobNormalizeNamingService;
    private readonly ILoggerFactory _loggerFactory;

    public BlobContainerFactory(
        IBlobContainerConfigurationProvider configurationProvider,
        IBlobProviderSelector providerSelector,
        ICurrentTenant currentTenant,
        IOptionsMonitor<BlobStoringOptions> optionsMonitor,
        IBlobNormalizeNamingService blobNormalizeNamingService,
        ILoggerFactory loggerFactory)
    {
        _configurationProvider = configurationProvider;
        _providerSelector = providerSelector;
        _currentTenant = currentTenant;
        _optionsMonitor = optionsMonitor;
        _blobNormalizeNamingService = blobNormalizeNamingService;
        _loggerFactory = loggerFactory;
    }

    public IBlobContainer Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var configuration = _configurationProvider.Get(name);
        var provider = _providerSelector.Get(name);

        return new BlobContainer(
            name,
            configuration,
            provider,
            _currentTenant,
            _optionsMonitor,
            _blobNormalizeNamingService,
            _loggerFactory.CreateLogger<BlobContainer>());
    }

    public IBlobContainer<TContainer> Create<TContainer>()
        where TContainer : class
    {
        var name = BlobContainerNameAttribute.GetContainerName<TContainer>();
        var innerContainer = Create(name);
        return new BlobContainer<TContainer>(innerContainer);
    }
}
