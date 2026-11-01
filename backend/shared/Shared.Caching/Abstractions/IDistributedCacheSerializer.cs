namespace Shared.Caching.Abstractions;

public interface IDistributedCacheSerializer
{
    byte[] Serialize<T>(T value);

    T? Deserialize<T>(byte[] bytes);
}
