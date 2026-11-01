using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Auditing;

public interface IAuditSerializer
{
    string Serialize(object obj);
}

public sealed class JsonAuditSerializer : IAuditSerializer, ITransientDependency
{
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly Lazy<JsonSerializerOptions> _serializerOptions;

    public JsonAuditSerializer(IOptions<SharedAuditingOptions> options)
    {
        _options = options;
        _serializerOptions = new Lazy<JsonSerializerOptions>(CreateSerializerOptions, isThreadSafe: true);
    }

    public string Serialize(object obj)
    {
        return JsonSerializer.Serialize(obj, _serializerOptions.Value);
    }

    private JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            MaxDepth = 16,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    jsonTypeInfo =>
                    {
                        if (ShouldIgnoreType(jsonTypeInfo.Type))
                        {
                            if (jsonTypeInfo.Kind == JsonTypeInfoKind.Object)
                            {
                                jsonTypeInfo.Properties.Clear();
                            }

                            return;
                        }

                        foreach (var property in jsonTypeInfo.Properties)
                        {
                            if (ShouldIgnoreType(property.PropertyType) ||
                                HasDisableAuditingAttribute(property.AttributeProvider) ||
                                property.PropertyType.IsDefined(typeof(DisableAuditingAttribute), inherit: true))
                            {
                                property.ShouldSerialize = static (_, _) => false;
                            }
                        }
                    }
                }
            }
        };
    }

    private bool ShouldIgnoreType(Type type)
    {
        return _options.Value.IgnoredTypes.Any(ignoredType => ignoredType.IsAssignableFrom(type)) ||
               type.IsDefined(typeof(DisableAuditingAttribute), inherit: true);
    }

    private static bool HasDisableAuditingAttribute(ICustomAttributeProvider? provider)
    {
        if (provider == null)
        {
            return false;
        }

        return provider.IsDefined(typeof(DisableAuditingAttribute), inherit: true);
    }
}
