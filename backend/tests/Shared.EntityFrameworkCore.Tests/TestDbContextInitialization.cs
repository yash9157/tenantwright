using Moq;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;

namespace Shared.EntityFrameworkCore.Tests;

internal static class TestDbContextInitialization
{
    public static TContext Initialize<TContext>(TContext dbContext)
        where TContext : ISharedEfCoreDbContext
    {
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
        unitOfWork.SetupGet(value => value.Options).Returns(new UnitOfWorkOptions());
        dbContext.Initialize(new EfCoreDbContextInitializationContext(unitOfWork.Object));
        return dbContext;
    }
}
