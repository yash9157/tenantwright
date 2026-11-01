namespace Shared.DistributedLocking.Abstractions;

public interface IDistributedLockKeyNormalizer
{
    string NormalizeKey(DistributedLockKeyNormalizeArgs args);
}

public sealed class DistributedLockKeyNormalizeArgs
{
    public string Name { get; }
    public bool IgnoreTenant { get; }

    public DistributedLockKeyNormalizeArgs(string name, bool ignoreTenant = false)
    {
        Name = name;
        IgnoreTenant = ignoreTenant;
    }
}
