using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.ApplicationUsers;
using Shared;

namespace Identity.Application.Contracts.Platform;

public sealed class PlatformRoleDto
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsStatic { get; set; }
    public bool IsPublic { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
    public int MemberCount { get; set; }
    public ApplicationRoleLifecycleState LifecycleState { get; set; }
    public long LifecycleVersion { get; set; }
    public Guid? RetirementRequestId { get; set; }
    public DateTime? RetirementRequestedAtUtc { get; set; }
}

public sealed class PlatformRoleMemberDto
{
    public string Id { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public bool IsActive { get; set; }
}

public sealed class RoleMembershipCardinalityReportDto
{
    public int SupportedMaximum { get; set; }
    public List<RoleMembershipCardinalityReportItemDto> Violations { get; set; } = [];
}

public sealed class RoleMembershipCardinalityReportItemDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = default!;
    public int ActiveRoleCount { get; set; }
}

public sealed class PlatformRoleCreateDto
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
}

public sealed class PlatformRoleUpdateDto
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>PlatformRoleAppService.ApplySorting</c>.
///
/// <c>MemberCount</c> is deliberately absent even though the list row carries it: the count comes from a
/// separate grouped query over user-role rows, so it is not a column this query can order by. Declaring it
/// would let a caller ask for an order the mapping cannot produce, and the request would silently come back
/// in name order instead.
/// </remarks>
[SortableFields(
    "Name",
    "Category",
    "LifecycleState",
    DefaultSort = "Name")]
public sealed class PlatformRoleListInput : PagedRequestDto
{
    /// <summary>
    /// Matches role name, category, or description, case-insensitively. Trimmed; empty means no filter.
    /// </summary>
    public string? Filter { get; set; }
}

public interface IPlatformRoleAppService
{
    Task<PlatformRoleDto> GetAsync(string id);

    /// <summary>
    /// The platform role list, paged and sorted.
    /// </summary>
    /// <remarks>
    /// Previously returned an unbounded <c>List&lt;PlatformRoleDto&gt;</c> with no paging, sorting, or
    /// clamp, which left the only ordering in the frontend — where it could reorder whatever had arrived
    /// but never the whole set.
    /// </remarks>
    Task<PagedResultDto<PlatformRoleDto>> GetListAsync(PlatformRoleListInput input);
    Task<PagedResultDto<PlatformRoleMemberDto>> GetMembersAsync(string id, PagedRequestDto input);
    Task<RoleMembershipCardinalityReportDto> GetMembershipCardinalityReportAsync();
    Task<PlatformRoleDto> CreateAsync(PlatformRoleCreateDto input);
    Task<PlatformRoleDto> UpdateAsync(string id, PlatformRoleUpdateDto input);
    Task<PlatformRoleDto> BeginRetirementAsync(string id);
    Task DeleteAsync(string id);
}
