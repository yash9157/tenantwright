using System.IO;
using System.Threading;

namespace Shared.BlobStoring;

public static class BlobContainerExtensions
{
    public static async Task SaveBytesAsync(
        this IBlobContainer container,
        string name,
        byte[] bytes,
        bool overrideExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(bytes);

        await using var memory = new MemoryStream(bytes, writable: false);
        await container.SaveAsync(name, memory, overrideExisting, cancellationToken);
    }

    public static async Task<byte[]> GetBytesAsync(
        this IBlobContainer container,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        await using var stream = await container.GetAsync(name, cancellationToken);
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    public static async Task<byte[]?> GetBytesOrNullAsync(
        this IBlobContainer container,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        await using var stream = await container.GetOrNullAsync(name, cancellationToken);
        if (stream == null)
        {
            return null;
        }

        await using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }
}
