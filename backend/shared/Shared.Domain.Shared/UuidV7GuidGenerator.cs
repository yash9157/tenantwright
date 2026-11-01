using System;

namespace Shared.Guids;

/// <summary>
/// Generates RFC 9562 UUID version 7 identifiers for TenantWright-owned records.
/// </summary>
public sealed class UuidV7GuidGenerator : IGuidGenerator
{
    public static UuidV7GuidGenerator Instance { get; } = new();

    private UuidV7GuidGenerator()
    {
    }

    public Guid Create() => Guid.CreateVersion7();
}
