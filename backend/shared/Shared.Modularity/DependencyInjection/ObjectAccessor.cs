namespace Shared.Modularity.DependencyInjection;

public sealed class ObjectAccessor<T> : IObjectAccessor<T>
{
    public T? Value { get; set; }

    public ObjectAccessor()
    {
    }

    public ObjectAccessor(T? value)
    {
        Value = value;
    }
}
