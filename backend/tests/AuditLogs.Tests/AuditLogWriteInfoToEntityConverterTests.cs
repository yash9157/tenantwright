using AuditLogs.Application.Events;
using AuditLogs.Domain.Shared.Auditing;
using Shared.EventContracts;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogWriteInfoToEntityConverterTests
{
    [Fact]
    public void Convert_should_map_audit_log_with_extra_properties_and_children()
    {
        var occurredAt = DateTime.SpecifyKind(new DateTime(2026, 2, 17, 11, 30, 0), DateTimeKind.Utc);
        var converter = new AuditLogWriteInfoToEntityConverter();

        var model = converter.Convert(new AuditLogWriteRequestedTenantEto
        {
            EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            OccurredAtUtc = occurredAt,
            SourceService = "identity",
            Category = "http",
            UserId = "user-1",
            UserName = "Jane",
            DurationMs = 20.4,
            ExtraProperties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["custom_key"] = "custom-value"
            },
            IsTruncated = true,
            PayloadBytes = 512,
            RedactionApplied = true,
            RedactionVersion = 1,
            Comments = ["comment-1"],
            Exceptions =
            [
                new AuditLogExceptionEto
                {
                    Type = "System.Exception",
                    Message = "boom"
                }
            ],
            Actions =
            [
                new AuditLogActionEto
                {
                    Name = "CreateUser",
                    DisplayName = "IdentityService",
                    DurationMs = 12,
                    Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["username"] = "jane"
                    }
                }
            ],
            EntityChanges =
            [
                new AuditLogEntityChangeEto
                {
                    ChangeType = "Updated",
                    EntityType = "Users.User",
                    EntityId = "42",
                    PropertyChanges =
                    [
                        new AuditLogEntityPropertyChangeEto
                        {
                            PropertyName = "Name",
                            OriginalValue = "Old",
                            NewValue = "New"
                        }
                    ]
                }
            ]
        }, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.Equal("identity", model.AuditLog.ApplicationName);
        Assert.Equal("http", model.AuditLog.Category);
        Assert.Equal(20, model.AuditLog.ExecutionDuration);
        Assert.True(model.AuditLog.ExtraProperties.ContainsKey("custom_key"));
        Assert.Equal("custom-value", model.AuditLog.ExtraProperties["custom_key"]?.ToString());
        Assert.Equal("True", model.AuditLog.ExtraProperties[AuditLogExtraPropertyNames.IsTruncated]?.ToString());
        Assert.Equal("512", model.AuditLog.ExtraProperties[AuditLogExtraPropertyNames.PayloadBytes]?.ToString());
        Assert.Single(model.Actions);
        Assert.Equal("IdentityService", model.Actions[0].ServiceName);
        Assert.Single(model.EntityChanges);
        Assert.Equal("Updated", model.EntityChanges[0].ChangeType);
        Assert.Single(model.EntityChanges[0].PropertyChanges);
    }

    [Fact]
    public void Convert_should_persist_signature_fields_onto_the_entity()
    {
        var occurredAt = DateTime.SpecifyKind(new DateTime(2026, 3, 6, 20, 0, 0), DateTimeKind.Utc);
        var signedAt = occurredAt.AddSeconds(1);
        var converter = new AuditLogWriteInfoToEntityConverter();

        var model = converter.Convert(new AuditLogWriteRequestedHostEto
        {
            EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            OccurredAtUtc = occurredAt,
            SourceService = "identity",
            Category = "http",
            SignatureKeyId = "key-1",
            SignatureTimestampUtc = signedAt,
            Signature = "ABCDEF0123456789"
        }, tenantId: null);

        Assert.Equal("key-1", model.AuditLog.SignatureKeyId);
        Assert.Equal(signedAt, model.AuditLog.SignatureTimestampUtc);
        Assert.Equal("ABCDEF0123456789", model.AuditLog.Signature);
    }

    [Fact]
    public void Convert_should_leave_signature_fields_null_when_eto_has_no_signature()
    {
        var converter = new AuditLogWriteInfoToEntityConverter();

        var model = converter.Convert(new AuditLogWriteRequestedHostEto
        {
            EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            OccurredAtUtc = DateTime.UtcNow,
            SourceService = "identity",
            Category = "http"
        }, tenantId: null);

        Assert.Null(model.AuditLog.SignatureKeyId);
        Assert.Null(model.AuditLog.SignatureTimestampUtc);
        Assert.Null(model.AuditLog.Signature);
    }
}
