namespace Workflows.Application.Contracts.Common;

public class PagedResultDto<T>
{
    public long TotalCount { get; set; }
    public List<T> Items { get; set; } = new();
}
