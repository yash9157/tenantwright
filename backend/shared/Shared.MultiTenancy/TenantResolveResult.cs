namespace Shared.MultiTenancy;

public enum TenantResolveStatus
{
    Unresolved = 0,
    Resolved = 1,
    InvalidInput = 2,
    AmbiguousInput = 3,
    TenantMismatch = 4
}

public sealed class TenantResolveResult
{
    public TenantResolveStatus Status { get; set; }

    public Guid? TenantId { get; set; }

    public string? Source { get; set; }

    public string? FailureReason { get; set; }

    public IReadOnlyList<string> AppliedResolvers => _appliedResolvers;

    private readonly List<string> _appliedResolvers = [];

    public void AddAppliedResolver(string resolverName)
    {
        if (string.IsNullOrWhiteSpace(resolverName))
        {
            return;
        }

        _appliedResolvers.Add(resolverName);
    }
}
