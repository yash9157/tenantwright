using System.IO;

namespace Shared.BlobStoring;

public interface IBlobProvider
{
    Task SaveAsync(BlobProviderSaveArgs args);

    Task<bool> DeleteAsync(BlobProviderDeleteArgs args);

    Task<bool> ExistsAsync(BlobProviderExistsArgs args);

    Task<Stream?> GetOrNullAsync(BlobProviderGetArgs args);

    Task<Uri> GetReadUrlAsync(BlobProviderReadUrlArgs args);
}
