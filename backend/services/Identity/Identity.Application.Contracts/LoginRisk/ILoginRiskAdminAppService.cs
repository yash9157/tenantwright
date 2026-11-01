using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Application.Contracts.LoginRisk;

public sealed class LoginRiskOverrideCreateInput
{
    public Guid UserId { get; set; }
    public IdentityLoginRiskOverrideMode ModeOverride { get; set; } = IdentityLoginRiskOverrideMode.Allow;
    public string? Reason { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public sealed class LoginRiskOverrideDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public IdentityLoginRiskOverrideMode ModeOverride { get; set; }
    public string? Reason { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
}

public sealed class LoginRiskOverrideListInput
{
    public Guid? UserId { get; set; }
    public IdentityLoginRiskOverrideMode? ModeOverride { get; set; }
    public bool IncludeInactive { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public sealed class PagedLoginRiskOverridesDto
{
    public long TotalCount { get; set; }
    public List<LoginRiskOverrideDto> Items { get; set; } = [];
}

public interface ILoginRiskAdminAppService
{
    Task<LoginRiskOverrideDto> CreateOverrideAsync(LoginRiskOverrideCreateInput input);
    Task<List<LoginRiskOverrideDto>> GetOverridesAsync(Guid? userId = null, bool includeInactive = false);
    Task<PagedLoginRiskOverridesDto> GetPagedOverridesAsync(LoginRiskOverrideListInput input);
    Task RemoveOverrideAsync(Guid id);
    Task MarkFalsePositiveAsync(Guid securityLogId);
}
