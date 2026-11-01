using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Shared.BackgroundTasks.Jobs;

public interface IBackgroundJobSerializer
{
    string SerializeArgs<TArgs>(TArgs args);
    object? DeserializeArgs(string json, Type argsType);
}

public sealed class SystemTextJsonBackgroundJobSerializer : IBackgroundJobSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IOptions<BackgroundTasksOptions> _options;

    public SystemTextJsonBackgroundJobSerializer(IOptions<BackgroundTasksOptions> options)
    {
        _options = options;
    }

    public string SerializeArgs<TArgs>(TArgs args)
    {
        ValidateArgsType(typeof(TArgs));

        var json = JsonSerializer.Serialize(args, SerializerOptions);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > _options.Value.MaxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Background job payload is {bytes} bytes, which exceeds the configured limit of {_options.Value.MaxPayloadBytes} bytes.");
        }

        return json;
    }

    public object? DeserializeArgs(string json, Type argsType)
    {
        ValidateArgsType(argsType);
        return JsonSerializer.Deserialize(json, argsType, SerializerOptions);
    }

    private static void ValidateArgsType(Type argsType)
    {
        if (typeof(Delegate).IsAssignableFrom(argsType) ||
            typeof(Stream).IsAssignableFrom(argsType) ||
            typeof(IServiceProvider).IsAssignableFrom(argsType) ||
            argsType == typeof(CancellationToken) ||
            argsType == typeof(Type) ||
            typeof(System.Reflection.MemberInfo).IsAssignableFrom(argsType))
        {
            throw new InvalidOperationException(
                $"Background job args type '{argsType.FullName}' is not supported. Use a compact serializable DTO.");
        }
    }
}
