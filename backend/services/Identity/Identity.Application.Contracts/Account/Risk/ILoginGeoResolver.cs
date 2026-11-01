namespace Identity.Application.Contracts.Account.Risk;

public interface ILoginGeoResolver
{
    string? ResolveCountryCode();
}
