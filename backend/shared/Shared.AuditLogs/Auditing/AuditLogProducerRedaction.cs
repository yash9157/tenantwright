using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs.Publishing;

namespace Shared.AuditLogs.Auditing;

internal readonly record struct AuditRedactionResult(
    int RedactionHits,
    int UnredactablePayloadCount);

internal static class AuditLogProducerRedaction
{
    public const int CurrentRedactionVersion = 1;

    public static int Apply(AuditLogInfo log, AuditRedactionOptions options)
        => ApplyWithResult(log, options).RedactionHits;

    public static AuditRedactionResult ApplyWithResult(AuditLogInfo log, AuditRedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(options);

        ApplyEntityChangeBudget(log);

        var redactionHits = 0;
        redactionHits += AuditLogRedactor.RedactProperties(log.ExtraProperties, options);
        var actionRedaction = RedactActions(log.Actions, options);
        redactionHits += actionRedaction.RedactionHits;
        redactionHits += RedactExceptions(log.Exceptions, options);
        redactionHits += RedactEntityChanges(log.EntityChanges, options);

        log.RedactionApplied = true;
        log.RedactionVersion = CurrentRedactionVersion;
        return new AuditRedactionResult(redactionHits, actionRedaction.UnredactablePayloadCount);
    }

    private static AuditRedactionResult RedactActions(IList<AuditLogActionInfo> actions, AuditRedactionOptions options)
    {
        var redactionHits = 0;
        var unredactablePayloadCount = 0;
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Parameters))
            {
                continue;
            }

            var result = AuditLogRedactor.RedactPayload(action.Parameters, options);
            switch (result.Outcome)
            {
                case AuditPayloadRedactionOutcome.Redacted:
                    action.Parameters = result.Payload ?? string.Empty;
                    redactionHits += result.RedactionHits;
                    break;
                case AuditPayloadRedactionOutcome.Unredactable:
                    unredactablePayloadCount++;
                    break;
            }
        }

        return new AuditRedactionResult(redactionHits, unredactablePayloadCount);
    }

    private static int RedactExceptions(IList<AuditLogExceptionInfo> exceptions, AuditRedactionOptions options)
    {
        var hits = 0;
        foreach (var exception in exceptions)
        {
            if (AuditLogRedactor.IsSensitiveKey(exception.Type, options))
            {
                exception.Type = options.Mask;
                hits++;
            }

            if (AuditLogRedactor.IsSensitiveKey(exception.Message, options))
            {
                exception.Message = options.Mask;
                hits++;
            }

            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                exception.StackTrace = null;
                hits++;
            }
        }

        return hits;
    }

    private static int RedactEntityChanges(IList<EntityChangeInfo> entityChanges, AuditRedactionOptions options)
    {
        var hits = 0;
        foreach (var entityChange in entityChanges)
        {
            foreach (var propertyChange in entityChange.PropertyChanges)
            {
                if (!AuditLogRedactor.IsSensitiveKey(propertyChange.PropertyName, options))
                {
                    continue;
                }

                propertyChange.OriginalValue = options.Mask;
                propertyChange.NewValue = options.Mask;
                hits++;
            }
        }

        return hits;
    }

    private static void ApplyEntityChangeBudget(AuditLogInfo log)
    {
        if (log.EntityChanges.Count > AuditLogWriteRequestValidation.MaxEntityChangeCount)
        {
            log.IsTruncated = true;
            log.EntityChanges.RemoveRange(
                AuditLogWriteRequestValidation.MaxEntityChangeCount,
                log.EntityChanges.Count - AuditLogWriteRequestValidation.MaxEntityChangeCount);
        }

        foreach (var change in log.EntityChanges)
        {
            if (change.PropertyChanges.Count <= AuditLogWriteRequestValidation.MaxPropertyChangeCountPerEntity)
            {
                continue;
            }

            log.IsTruncated = true;
            change.PropertyChanges.RemoveRange(
                AuditLogWriteRequestValidation.MaxPropertyChangeCountPerEntity,
                change.PropertyChanges.Count - AuditLogWriteRequestValidation.MaxPropertyChangeCountPerEntity);
        }
    }
}
