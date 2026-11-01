using System;
using Shared.Modularity.DependencyInjection;

namespace Shared.EntityFrameworkCore;

public sealed class UtcClock : IClock, ISingletonDependency
{
    public DateTime Now => DateTime.UtcNow;

    public DateTime Normalize(DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Utc)
        {
            return dateTime;
        }

        if (dateTime.Kind == DateTimeKind.Local)
        {
            return dateTime.ToUniversalTime();
        }

        return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
    }
}
