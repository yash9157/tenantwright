using System;

namespace Shared.Data.Exceptions;

public class SharedDbConcurrencyException : Exception
{
    public SharedDbConcurrencyException()
    {

    }

    public SharedDbConcurrencyException(string message)
        : base(message)
    {

    }

    public SharedDbConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {

    }
}
