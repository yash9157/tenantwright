using System.Threading.Tasks;

namespace Shared.Data.ConnectionStrings;

public interface IConnectionStringResolver
{
    Task<string> ResolveAsync(string? connectionStringName = null);
}
