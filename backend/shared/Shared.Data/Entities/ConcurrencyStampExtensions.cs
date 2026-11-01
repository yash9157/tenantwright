using System;
using Shared.Domain;

namespace Shared.Data.Entities;

public static class ConcurrencyStampExtensions
{
    public static void SetConcurrencyStampIfNotNull(this IHasConcurrencyStamp entity, string? concurrencyStamp)
    {
        if (!string.IsNullOrEmpty(concurrencyStamp))
        {
            entity.ConcurrencyStamp = concurrencyStamp!;
        }
    }
}
