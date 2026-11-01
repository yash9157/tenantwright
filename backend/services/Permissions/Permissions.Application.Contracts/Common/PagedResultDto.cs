using System.Collections.Generic;

namespace Permissions.Application.Contracts.Common;

/// <summary>
/// Minimal paged result envelope for Permissions list endpoints: the total count of matching
/// records (before paging) plus the current page of items.
/// </summary>
public sealed class PagedResultDto<T>
{
    public long TotalCount { get; set; }

    public List<T> Items { get; set; } = [];
}
