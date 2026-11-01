using System;
using System.Collections.Generic;

namespace Shared.EntityFrameworkCore.ConnectionStrings;

public class TenantConnectionStringsCacheItem
{
    public bool Exists { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Decrypted dedicated connection strings, keyed by dedicatable catalog name only.</summary>
    public Dictionary<string, string> ConnectionStrings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Dedicatable names whose dedicated connection string targets this service's shared database. Such an
    /// assignment is a misconfiguration (it would silently mix the tenant back into shared storage while fan-out
    /// treats it as dedicated), so resolution fails closed for these names until the assignment is corrected.
    /// </summary>
    public HashSet<string> NamesTargetingSharedDatabase { get; set; } = new(StringComparer.Ordinal);
}
