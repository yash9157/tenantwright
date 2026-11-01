using Shared.Caching.Abstractions;
using Shared.Caching.Serialization;

namespace Shared.Caching.Implementations;

public class DefaultDistributedCacheSerializer : IDistributedCacheSerializer
{
    private readonly CacheSerializer _serializer;

    public DefaultDistributedCacheSerializer(CacheSerializer serializer)
    {
        _serializer = serializer;
    }

    public byte[] Serialize<T>(T value)
    {
        return _serializer.Serialize(value);
    }

    public T? Deserialize<T>(byte[] bytes)
    {
        return _serializer.Deserialize<T>(bytes);
    }
}
