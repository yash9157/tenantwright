using System;
using System.Linq;
using Shared.Modularity;

namespace Shared.Events;

[AttributeUsage(AttributeTargets.Class)]
public class EventNameAttribute : Attribute, IEventNameProvider
{
    public virtual string Name { get; }

    public EventNameAttribute(string name)
    {
        Name = Check.NotNullOrWhiteSpace(name, nameof(name));
    }

    public static string GetNameOrDefault<TEvent>()
    {
        return GetNameOrDefault(typeof(TEvent));
    }

    public static string GetNameOrDefault(Type eventType)
    {
        Check.NotNull(eventType, nameof(eventType));

        return (eventType
                    .GetCustomAttributes(true)
                    .OfType<IEventNameProvider>()
                    .FirstOrDefault()
                    ?.GetName(eventType)
                ?? eventType.FullName)!;
    }

    public string GetName(Type eventType)
    {
        return Name;
    }
}
