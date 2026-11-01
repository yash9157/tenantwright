using System;

namespace Shared.EntityFrameworkCore;

public interface IClock
{
    DateTime Now { get; }
    DateTime Normalize(DateTime dateTime);
}
