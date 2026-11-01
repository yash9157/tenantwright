namespace Shared.BlobStoring;

public class BlobNotFoundException : InvalidOperationException
{
    public BlobNotFoundException(string message)
        : base(message)
    {
    }
}
