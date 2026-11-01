using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Shared.Caching.Abstractions;

public interface IExceptionNotifier
{
    Task NotifyAsync(ExceptionNotificationContext context);
}

public class ExceptionNotificationContext
{
    public Exception Exception { get; }

    public LogLevel LogLevel { get; }

    public ExceptionNotificationContext(Exception exception, LogLevel logLevel = LogLevel.Error)
    {
        Exception = exception;
        LogLevel = logLevel;
    }
}
