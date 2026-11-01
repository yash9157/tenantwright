using Microsoft.Extensions.Options;

namespace Shared.BlobStoring;

public sealed class DefaultBlobContainerConfigurationProvider : IBlobContainerConfigurationProvider
{
    private readonly IOptionsMonitor<BlobStoringOptions> _optionsMonitor;

    public DefaultBlobContainerConfigurationProvider(IOptionsMonitor<BlobStoringOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public BlobContainerConfiguration Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _optionsMonitor.CurrentValue.Containers.GetConfiguration(name);
    }
}
