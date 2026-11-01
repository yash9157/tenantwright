namespace Shared.BlobStoring;

public class BlobConfigurationException : InvalidOperationException
{
    public BlobConfigurationException(string message)
        : base(message)
    {
    }
}
