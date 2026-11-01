namespace Shared.BlobStoring;

public interface IBlobProviderSelector
{
    IBlobProvider Get(string containerName);
}
