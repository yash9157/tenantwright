using System;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shared;

namespace Shared.EntityFrameworkCore.ExceptionTranslation;

/// <summary>
/// Converts PostgreSQL persistence failures into provider-neutral exceptions with client-safe details.
/// </summary>
public static class PostgreSqlDatabaseExceptionTranslator
{
    private const string GenericUniqueViolationMessage = "The request conflicts with existing data.";
    private const string GenericForeignKeyViolationMessage = "The request conflicts with related data.";
    private const string GenericCheckViolationMessage = "The request violates a data validation rule.";
    private const string GenericTransactionConflictMessage = "The request conflicted with a concurrent transaction. Please try again.";

    public static bool TryTranslate(
        DbUpdateException exception,
        Type dbContextType,
        SharedDatabaseExceptionTranslationOptions options,
        out SharedException translatedException)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(dbContextType);
        ArgumentNullException.ThrowIfNull(options);

        var postgresException = FindPostgresException(exception);
        if (postgresException is null)
        {
            translatedException = default!;
            return false;
        }

        if (options.TryGetDescriptor(dbContextType, postgresException.ConstraintName, out var descriptor))
        {
            translatedException = CreateDescriptorException(descriptor, exception);
            return true;
        }

        translatedException = postgresException.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new SharedConflictException(
                SharedErrorCodes.UniqueConstraintViolation,
                GenericUniqueViolationMessage,
                exception),
            PostgresErrorCodes.ForeignKeyViolation => new SharedConflictException(
                SharedErrorCodes.ForeignKeyConflict,
                GenericForeignKeyViolationMessage,
                exception),
            PostgresErrorCodes.CheckViolation => new SharedValidationException(
                SharedErrorCodes.CheckConstraintViolation,
                GenericCheckViolationMessage,
                innerException: exception),
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected =>
                CreateTransactionConflictException(exception),
            _ => default!
        };

        return translatedException is not null;
    }

    /// <summary>
    /// Translates the wrapper EF Core raises when Npgsql reports a transient failure.
    /// </summary>
    /// <remarks>
    /// Npgsql classifies <c>40001</c> and <c>40P01</c> as transient. With the default non-retrying execution
    /// strategy, EF Core's <c>ExecutionStrategy</c> wraps such a failure in an
    /// <see cref="InvalidOperationException"/> before it reaches the caller, so the
    /// <see cref="DbUpdateException"/> never surfaces and <see cref="TryTranslate"/> is never reached. Left
    /// alone, two callers racing the same protected write produce one success and one server fault, where the
    /// second is really a conflict.
    ///
    /// Deliberately narrow. Only a wrapper whose inner chain carries a PostgreSQL exception with a recognized
    /// transient state is translated; every other <see cref="InvalidOperationException"/> — including one
    /// carrying an unrecognized PostgreSQL state — is left for the caller to rethrow unchanged. This does not
    /// retry anything: the write already failed and is reported as a conflict, not reattempted.
    /// </remarks>
    public static bool TryTranslateTransientWrapper(
        InvalidOperationException exception,
        out SharedException translatedException)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var postgresException = FindPostgresException(exception);
        if (postgresException is null || !IsTransientConflictState(postgresException.SqlState))
        {
            translatedException = default!;
            return false;
        }

        translatedException = CreateTransactionConflictException(exception);
        return true;
    }

    /// <summary>
    /// The PostgreSQL states that mean "a concurrent transaction won; this one was aborted". Both paths use this
    /// so a wrapped failure and a direct one cannot be classified differently.
    /// </summary>
    private static bool IsTransientConflictState(string? sqlState) =>
        sqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private static SharedConflictException CreateTransactionConflictException(Exception exception) =>
        new(SharedErrorCodes.TransactionConflict, GenericTransactionConflictMessage, exception);

    private static SharedException CreateDescriptorException(
        SharedDatabaseConstraintDescriptor descriptor,
        DbUpdateException exception)
    {
        return descriptor.Category switch
        {
            SharedDatabaseConstraintCategory.Validation => new SharedValidationException(
                descriptor.Code,
                descriptor.Message,
                innerException: exception),
            _ => new SharedConflictException(
                descriptor.Code,
                descriptor.Message,
                exception)
        };
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }
}
