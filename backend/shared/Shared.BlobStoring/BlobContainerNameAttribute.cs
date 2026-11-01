using System.Reflection;

namespace Shared.BlobStoring;

[AttributeUsage(AttributeTargets.Class)]
public sealed class BlobContainerNameAttribute : Attribute
{
    public string Name { get; }

    public BlobContainerNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string GetName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Name;
    }

    public static string GetContainerName<T>()
    {
        return GetContainerName(typeof(T));
    }

    public static string GetContainerName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var attribute = type.GetCustomAttribute<BlobContainerNameAttribute>();
        if (attribute != null)
        {
            return attribute.GetName(type);
        }

        return type.FullName ?? type.Name;
    }
}
