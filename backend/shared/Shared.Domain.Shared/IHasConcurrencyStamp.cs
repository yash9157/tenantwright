namespace Shared.Domain;

public interface IHasConcurrencyStamp
{
    string ConcurrencyStamp { get; set; }
}
