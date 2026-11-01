namespace Shared.Modularity.Modules;

public class ModuleDescriptor
{
    public Type Type { get; }
    public ISharedModule Instance { get; }

    public ModuleDescriptor(Type type, ISharedModule instance)
    {
        Type = type;
        Instance = instance;
    }
}
