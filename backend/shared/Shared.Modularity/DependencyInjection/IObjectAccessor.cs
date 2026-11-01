namespace Shared.Modularity.DependencyInjection;

public interface IObjectAccessor<out T>
{
    T? Value { get; }
}
