using System.Threading.Tasks;

namespace Shared.Data.ConnectionStrings;

public interface IConnectionStringChecker
{
    Task<SharedConnectionStringCheckResult> CheckAsync(string connectionString);
}
