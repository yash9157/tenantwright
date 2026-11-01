namespace Shared.EntityFrameworkCore;

public interface ISharedEfCoreDbContext : IEfCoreDbContext
{
    void Initialize(EfCoreDbContextInitializationContext initializationContext);
}
