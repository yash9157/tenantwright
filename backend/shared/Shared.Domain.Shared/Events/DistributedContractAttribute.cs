namespace Shared.Events;

public enum DistributedContractScope
{
    HostOnly = 1,
    TenantRequired = 2
}

public enum DistributedContractDataClassification
{
    Internal = 1,
    Sensitive = 2
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DistributedContractAttribute : Attribute
{
    public DistributedContractAttribute(
        int schemaVersion,
        DistributedContractScope scope,
        DistributedContractDataClassification dataClassification)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Schema version must be greater than zero.");
        }

        SchemaVersion = schemaVersion;
        Scope = scope;
        DataClassification = dataClassification;
    }

    public int SchemaVersion { get; }

    public DistributedContractScope Scope { get; }

    public DistributedContractDataClassification DataClassification { get; }
}
