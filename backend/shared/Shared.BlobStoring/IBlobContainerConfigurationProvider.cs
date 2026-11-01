namespace Shared.BlobStoring;

public interface IBlobContainerConfigurationProvider
{
    BlobContainerConfiguration Get(string name);
}
