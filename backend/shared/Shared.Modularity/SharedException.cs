using System;
using System.Runtime.Serialization;

namespace Shared;

/// <summary>
/// Represents a coded application error. Diagnostic messages are internal by
/// default; caller-safe details must be supplied explicitly by a client-facing
/// derived exception.
/// </summary>
public class SharedException : Exception
{
    private const string DefaultMessage = "The request could not be completed.";

    public string Code { get; }

    public string? PublicDetail { get; }

    public SharedException()
        : this(SharedErrorCodes.RequestFailed, DefaultMessage)
    {
    }

    public SharedException(string? message)
        : this(SharedErrorCodes.RequestFailed, message)
    {
    }

    public SharedException(string? message, Exception? innerException)
        : this(SharedErrorCodes.RequestFailed, message, innerException)
    {
    }

    public SharedException(string code, string? message)
        : this(code, message, null)
    {
    }

    public SharedException(string code, string? message, Exception? innerException)
        : this(code, message, publicDetail: null, innerException)
    {
    }

    protected SharedException(
        string code,
        string? message,
        string? publicDetail,
        Exception? innerException)
        : base(message ?? DefaultMessage, innerException)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                "Error codes must use lower-snake-case letters, digits, and underscores.",
                nameof(code));
        }

        Code = code;
        PublicDetail = string.IsNullOrWhiteSpace(publicDetail) ? null : publicDetail;
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    protected SharedException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
        Code = SharedErrorCodes.RequestFailed;
        PublicDetail = null;
    }

    private static bool IsValidCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        foreach (var character in code)
        {
            if (!((character >= 'a' && character <= 'z') ||
                  (character >= '0' && character <= '9') ||
                  character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
