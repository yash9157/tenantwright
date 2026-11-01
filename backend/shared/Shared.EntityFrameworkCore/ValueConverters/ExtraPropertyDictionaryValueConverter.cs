using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Shared.Domain;

namespace Shared.EntityFrameworkCore.ValueConverters;

public class ExtraPropertyDictionaryValueConverter : ValueConverter<ExtraPropertyDictionary, string>
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ExtraPropertyDictionaryValueConverter()
        : base(
            v => Serialize(v),
            v => Deserialize(v))
    {
    }

    private static string Serialize(ExtraPropertyDictionary extraProperties)
    {
        return JsonSerializer.Serialize(extraProperties ?? new ExtraPropertyDictionary(), SerializerOptions);
    }

    private static ExtraPropertyDictionary Deserialize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ExtraPropertyDictionary();
        }

        return JsonSerializer.Deserialize<ExtraPropertyDictionary>(value, SerializerOptions) ?? new ExtraPropertyDictionary();
    }
}
