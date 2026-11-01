using System;

namespace Shared.EntityFrameworkCore;

public interface IEfCoreDbContextTypeProvider
{
    Type GetDbContextType(Type dbContextType);
}
