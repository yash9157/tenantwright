using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shared.EntityFrameworkCore.ValueConverters;

public class SharedDateTimeValueConverter : ValueConverter<DateTime, DateTime>
{
    public SharedDateTimeValueConverter(IClock clock, ConverterMappingHints? mappingHints = null)
        : base(
            x => clock.Normalize(x),
            x => clock.Normalize(x), mappingHints)
    {
    }
}

public class SharedNullableDateTimeValueConverter : ValueConverter<DateTime?, DateTime?>
{
    public SharedNullableDateTimeValueConverter(IClock clock, ConverterMappingHints? mappingHints = null)
        : base(
            x => x.HasValue ? clock.Normalize(x.Value) : x,
            x => x.HasValue ? clock.Normalize(x.Value) : x, mappingHints)
    {
    }
}
