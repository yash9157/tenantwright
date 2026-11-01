using System.Threading.Tasks;

namespace Shared.EntityFrameworkCore;

public interface IDbContextProvider<TDbContext>
    where TDbContext : IEfCoreDbContext
{
    Task<TDbContext> GetDbContextAsync();
}
