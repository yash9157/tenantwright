using System.Threading.Tasks;

namespace Shared.Data.Seeding;

public interface IDataSeedContributor
{
    Task SeedAsync(DataSeedContext context);
}
