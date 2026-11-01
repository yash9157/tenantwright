using System.Text.Json;

namespace Shared.Caching.Serialization;

public class CacheSerializer
{
    private readonly JsonSerializerOptions _jsonOptions;

    public CacheSerializer(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public byte[] Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
    }

    public T? Deserialize<T>(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
    }
}
