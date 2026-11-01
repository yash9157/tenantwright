using Shared.EntityFrameworkCore;

namespace AuditLogs.Tests.TestDoubles;

internal sealed class StaticDbContextProvider<TDbContext> : IDbContextProvider<TDbContext>
    where TDbContext : class, IEfCoreDbContext
{
    private readonly TDbContext _dbContext;

    public StaticDbContextProvider(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<TDbContext> GetDbContextAsync() => Task.FromResult(_dbContext);
}
