namespace Shared.BlobStoring;

public interface IBlobStoringConfigurationValidator
{
    void Validate(bool isDevelopment);
}
