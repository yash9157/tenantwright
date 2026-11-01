namespace Shared.Uow.Options;

public class UnitOfWorkMiddlewareOptions
{
    public const string SectionName = "UnitOfWork:Middleware";

    public List<string> IgnoredUrls { get; } = ["/health", "/alive"];
}
