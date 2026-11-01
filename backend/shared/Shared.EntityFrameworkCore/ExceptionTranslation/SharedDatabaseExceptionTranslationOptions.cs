using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Shared.EntityFrameworkCore.ExceptionTranslation;

/// <summary>
/// Maps known database constraints to safe, domain-owned API error descriptions.
/// Unknown constraints deliberately use the translator's generic safe messages.
/// </summary>
public sealed class SharedDatabaseExceptionTranslationOptions
{
    private readonly Dictionary<ConstraintKey, SharedDatabaseConstraintDescriptor> _descriptors = new();

    public void Configure<TDbContext>(
        string constraintName,
        SharedDatabaseConstraintDescriptor descriptor)
        where TDbContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        ArgumentNullException.ThrowIfNull(descriptor);

        _descriptors[new ConstraintKey(typeof(TDbContext), constraintName)] = descriptor;
    }

    internal bool TryGetDescriptor(
        Type dbContextType,
        string? constraintName,
        out SharedDatabaseConstraintDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(constraintName))
        {
            descriptor = default!;
            return false;
        }

        return _descriptors.TryGetValue(
            new ConstraintKey(dbContextType, constraintName),
            out descriptor!);
    }

    private readonly record struct ConstraintKey(Type DbContextType, string ConstraintName);
}

public enum SharedDatabaseConstraintCategory
{
    Conflict = 0,
    Validation = 1
}

public sealed record SharedDatabaseConstraintDescriptor(
    string Code,
    string Message,
    SharedDatabaseConstraintCategory Category = SharedDatabaseConstraintCategory.Conflict);
