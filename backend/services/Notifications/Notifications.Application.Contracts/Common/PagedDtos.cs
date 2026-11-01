namespace Notifications.Application.Contracts.Common;

public class PagedRequestDto
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public class PagedResultDto<T>
{
    public long TotalCount { get; set; }
    public List<T> Items { get; set; } = new();
}
