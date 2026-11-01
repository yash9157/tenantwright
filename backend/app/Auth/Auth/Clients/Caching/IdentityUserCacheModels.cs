namespace Auth.Clients.Caching;

public sealed class IdentityProfileCacheItem
{
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? ProfileVersion { get; set; }
}

public sealed class IdentityAuthorizationCacheItem
{
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<Guid> RoleIds { get; set; } = new();
    public bool IsActive { get; set; }
    public bool IsLockedOut { get; set; }
    public string? AuthzVersion { get; set; }
}

public sealed class IdentityAliasCacheItem
{
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
}

public sealed class IdentityProfileCacheEnvelope
{
    public IdentityProfileCacheItem Payload { get; set; } = new();
    public DateTimeOffset FetchedAtUtc { get; set; }
    public DateTimeOffset SoftExpireAtUtc { get; set; }
    public DateTimeOffset HardExpireAtUtc { get; set; }
    public string? ETag { get; set; }
}

public sealed class IdentityAuthorizationCacheEnvelope
{
    public IdentityAuthorizationCacheItem Payload { get; set; } = new();
    public DateTimeOffset FetchedAtUtc { get; set; }
    public DateTimeOffset SoftExpireAtUtc { get; set; }
    public DateTimeOffset HardExpireAtUtc { get; set; }
    public string? ETag { get; set; }
}

public sealed class IdentityAliasCacheEnvelope
{
    public IdentityAliasCacheItem Payload { get; set; } = new();
    public DateTimeOffset FetchedAtUtc { get; set; }
    public DateTimeOffset SoftExpireAtUtc { get; set; }
    public DateTimeOffset HardExpireAtUtc { get; set; }
}
