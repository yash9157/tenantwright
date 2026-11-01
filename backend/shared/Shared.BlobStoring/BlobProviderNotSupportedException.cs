namespace Shared.BlobStoring;

public class BlobProviderNotSupportedException : NotSupportedException
{
    public BlobProviderNotSupportedException(string message)
        : base(message)
    {
    }
}
