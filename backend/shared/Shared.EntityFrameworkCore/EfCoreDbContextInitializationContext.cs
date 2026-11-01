using Shared.Uow.Abstractions;

namespace Shared.EntityFrameworkCore;

public sealed class EfCoreDbContextInitializationContext
{
    public EfCoreDbContextInitializationContext(IUnitOfWork unitOfWork)
        : this((unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork))).Options.Timeout)
    {
    }

    public EfCoreDbContextInitializationContext(int? timeoutMilliseconds)
    {
        if (timeoutMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                timeoutMilliseconds,
                "A DbContext command timeout must be greater than zero when specified.");
        }

        TimeoutMilliseconds = timeoutMilliseconds;
    }

    public int? TimeoutMilliseconds { get; }
}
