namespace Shared.BackgroundTasks.Jobs;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BackgroundJobNameAttribute : Attribute
{
    public string Name { get; }

    public BackgroundJobNameAttribute(string name)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Background job name cannot be empty.", nameof(name))
            : name.Trim();
    }

    public static string GetName(Type argsType)
    {
        var attribute = argsType.GetCustomAttributes(typeof(BackgroundJobNameAttribute), inherit: false)
            .OfType<BackgroundJobNameAttribute>()
            .FirstOrDefault();

        return attribute?.Name ?? argsType.FullName ?? argsType.Name;
    }
}
