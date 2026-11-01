namespace Workflows.Application.Contracts.Common;

public class PagedRequestDto
{
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}
