using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;

namespace Shared.AspNetCore.Host.Logging;

public sealed class ActivityTraceEnricher : ILogEventEnricher
{
    private const string TraceIdPropertyName = "trace_id";
    private const string SpanIdPropertyName = "span_id";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        var traceId = activity.TraceId.ToString();
        var spanId = activity.SpanId.ToString();

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(TraceIdPropertyName, traceId));
        }

        if (!string.IsNullOrWhiteSpace(spanId))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(SpanIdPropertyName, spanId));
        }
    }
}
