namespace Identity.Application.Contracts.Users;

public class PagedRequestDto
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
    public string? Sorting { get; set; }
}

public class PagedResultDto<T>
{
    public long TotalCount { get; set; }
    public List<T> Items { get; set; } = [];
}
