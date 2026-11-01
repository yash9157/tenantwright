using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Shared;

namespace Identity.Application.Contracts.ClaimTypes;

public class ClaimTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public bool Required { get; set; }
    public bool IsStatic { get; set; }
    public string? Regex { get; set; }
    public string? RegexDescription { get; set; }
    public string? Description { get; set; }
    public string ValueType { get; set; } = default!;
}

public class ClaimTypeCreateDto
{
    public string Name { get; set; } = default!;
    public bool Required { get; set; }
    public string? Regex { get; set; }
    public string? RegexDescription { get; set; }
    public string? Description { get; set; }
    public string ValueType { get; set; } = "String";
}

public class ClaimTypeUpdateDto
{
    public bool Required { get; set; }
    public string? Regex { get; set; }
    public string? RegexDescription { get; set; }
    public string? Description { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>IdentityClaimTypeAppService.ApplySorting</c>.
///
/// <c>ValueType</c> orders by the stored enum, so the order is the enum's declaration order rather than the
/// alphabetical order of the strings the DTO exposes. That is the ordering the database can index, and the
/// grouping it produces — all of one value type together — is what an operator scanning the column wants.
/// </remarks>
[SortableFields(
    "Name",
    "ValueType",
    "Required",
    DefaultSort = "Name")]
public sealed class ClaimTypeListInput : PagedRequestDto
{
    /// <summary>
    /// Matches claim-type name or description, case-insensitively. Trimmed; empty means no filter.
    /// </summary>
    public string? Filter { get; set; }
}

public interface IIdentityClaimTypeAppService
{
    Task<ClaimTypeDto> GetAsync(Guid id);
    /// <summary>
    /// The calling tenant's claim types, paged and sorted.
    /// </summary>
    /// <remarks>
    /// Previously returned an unbounded <c>List&lt;ClaimTypeDto&gt;</c>. Both this and
    /// <see cref="GetPlatformListAsync"/> are paged together because they run the same query — leaving one
    /// unbounded would have meant a second copy of it.
    /// </remarks>
    Task<PagedResultDto<ClaimTypeDto>> GetListAsync(ClaimTypeListInput input);
    Task<ClaimTypeDto> CreateAsync(ClaimTypeCreateDto input);
    Task<ClaimTypeDto> UpdateAsync(Guid id, ClaimTypeUpdateDto input);
    Task DeleteAsync(Guid id);
    Task<ClaimTypeDto> GetPlatformAsync(Guid tenantId, Guid id);
    /// <inheritdoc cref="GetListAsync"/>
    Task<PagedResultDto<ClaimTypeDto>> GetPlatformListAsync(Guid tenantId, ClaimTypeListInput input);
    Task<ClaimTypeDto> CreatePlatformAsync(Guid tenantId, ClaimTypeCreateDto input);
    Task<ClaimTypeDto> UpdatePlatformAsync(Guid tenantId, Guid id, ClaimTypeUpdateDto input);
    Task DeletePlatformAsync(Guid tenantId, Guid id);
}
