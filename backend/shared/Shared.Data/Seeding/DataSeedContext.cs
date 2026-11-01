using System;
using System.Collections.Generic;

namespace Shared.Data.Seeding;

public class DataSeedContext
{
    public Guid? TenantId { get; set; }

    public object? this[string name]
    {
        get => Properties.GetValueOrDefault(name);
        set => Properties[name] = value;
    }

    public Dictionary<string, object?> Properties { get; }

    public DataSeedContext(Guid? tenantId = null)
    {
        TenantId = tenantId;
        Properties = new Dictionary<string, object?>();
    }

    public virtual DataSeedContext WithProperty(string key, object? value)
    {
        Properties[key] = value;
        return this;
    }
}
