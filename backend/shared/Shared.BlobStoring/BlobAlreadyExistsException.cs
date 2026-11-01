namespace Shared.BlobStoring;

public class BlobAlreadyExistsException : InvalidOperationException
{
    public BlobAlreadyExistsException(string message)
        : base(message)
    {
    }
}
