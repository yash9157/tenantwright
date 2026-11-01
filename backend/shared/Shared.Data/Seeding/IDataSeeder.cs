using System.Threading.Tasks;

namespace Shared.Data.Seeding;

public interface IDataSeeder
{
    Task SeedAsync(DataSeedContext context);
}
