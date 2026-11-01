using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;

namespace Shared;

[Serializable]
public class SharedValidationException : SharedException
{
    private const string DefaultMessage = "The request is invalid.";

    public IReadOnlyDictionary<string, string[]>? Errors { get; }

    public SharedValidationException()
        : this(DefaultMessage)
    {
    }

    public SharedValidationException(string? message)
        : this(SharedErrorCodes.ValidationFailed, message, null, null)
    {
    }

    public SharedValidationException(string? message, Exception? innerException)
        : this(SharedErrorCodes.ValidationFailed, message, null, innerException)
    {
    }

    public SharedValidationException(
        string? message,
        IReadOnlyDictionary<string, string[]>? errors)
        : this(SharedErrorCodes.ValidationFailed, message, errors, null)
    {
    }

    public SharedValidationException(
        string code,
        string? message,
        IReadOnlyDictionary<string, string[]>? errors = null,
        Exception? innerException = null)
        : base(code, message ?? DefaultMessage, message ?? DefaultMessage, innerException)
    {
        Errors = CopyErrors(errors);
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    protected SharedValidationException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
    }

    private static ReadOnlyDictionary<string, string[]>? CopyErrors(IReadOnlyDictionary<string, string[]>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return null;
        }

        return new ReadOnlyDictionary<string, string[]>(
            errors.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal));
    }
}

[Serializable]
public class SharedConflictException : SharedException
{
    private const string DefaultMessage = "The request conflicts with the current state.";

    public SharedConflictException()
        : this(DefaultMessage)
    {
    }

    public SharedConflictException(string? message)
        : this(SharedErrorCodes.Conflict, message, null)
    {
    }

    public SharedConflictException(string? message, Exception? innerException)
        : this(SharedErrorCodes.Conflict, message, innerException)
    {
    }

    public SharedConflictException(string code, string? message, Exception? innerException = null)
        : base(code, message ?? DefaultMessage, message ?? DefaultMessage, innerException)
    {
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    protected SharedConflictException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
    }
}

[Serializable]
public class SharedUnauthorizedException : SharedException
{
    private const string DefaultMessage = "Authentication is required.";

    public SharedUnauthorizedException()
        : this(DefaultMessage)
    {
    }

    public SharedUnauthorizedException(string? message)
        : this(SharedErrorCodes.Unauthorized, message, null)
    {
    }

    public SharedUnauthorizedException(string? message, Exception? innerException)
        : this(SharedErrorCodes.Unauthorized, message, innerException)
    {
    }

    public SharedUnauthorizedException(string code, string? message, Exception? innerException = null)
        : base(code, message ?? DefaultMessage, message ?? DefaultMessage, innerException)
    {
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    protected SharedUnauthorizedException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
    }
}

[Serializable]
public class SharedForbiddenException : SharedException
{
    private const string DefaultMessage = "You are not allowed to perform this action.";

    public SharedForbiddenException()
        : this(DefaultMessage)
    {
    }

    public SharedForbiddenException(string? message)
        : this(SharedErrorCodes.Forbidden, message, null)
    {
    }

    public SharedForbiddenException(string? message, Exception? innerException)
        : this(SharedErrorCodes.Forbidden, message, innerException)
    {
    }

    public SharedForbiddenException(string code, string? message, Exception? innerException = null)
        : base(code, message ?? DefaultMessage, message ?? DefaultMessage, innerException)
    {
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    protected SharedForbiddenException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
    }
}

[Serializable]
public class SharedTimeoutException : SharedException
{
    private const string DefaultMessage = "The request timed out.";

    public SharedTimeoutException()
        : this(DefaultMessage)
    {
    }

    public SharedTimeoutException(string? message)
        : this(SharedErrorCodes.RequestTimeout, message, null)
    {
    }

    public SharedTimeoutException(string? message, Exception? innerException)
        : this(SharedErrorCodes.RequestTimeout, message, innerException)
    {
    }

    public SharedTimeoutException(string code, string? message, Exception? innerException = null)
        : base(code, message ?? DefaultMessage, message ?? DefaultMessage, innerException)
    {
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    protected SharedTimeoutException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
    }
}
