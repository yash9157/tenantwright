namespace Shared.Data.Seeding;

public class SharedDataSeedOptions
{
    public DataSeedContributorList Contributors { get; }

    public SharedDataSeedOptions()
    {
        Contributors = new DataSeedContributorList();
    }
}
