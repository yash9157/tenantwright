using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs.Publishing;
using Xunit;

namespace Shared.AuditLogs.Tests;

public class AuditLogRedactorTests
{
    [Fact]
    public void RedactProperties_should_use_configured_sensitive_keys_and_mask()
    {
        var options = new AuditRedactionOptions
        {
            Mask = "[MASKED]",
            SensitiveKeys = ["pin"]
        };
        AuditRedactionOptionsPostConfigure.Normalize(options);

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["cardPin"] = "1234",
            ["displayName"] = "Jane"
        };

        var hits = AuditLogRedactor.RedactProperties(properties, options);

        Assert.Equal(1, hits);
        Assert.Equal("[MASKED]", properties["cardPin"]);
        Assert.Equal("Jane", properties["displayName"]);
    }

    [Fact]
    public void RedactProperties_should_redact_nested_json_payload_values()
    {
        var options = new AuditRedactionOptions
        {
            Mask = "[MASKED]",
            SensitiveKeys = ["password"]
        };
        AuditRedactionOptionsPostConfigure.Normalize(options);

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["payload"] = "{\"profile\":{\"password\":\"secret\",\"displayName\":\"Jane\"}}"
        };

        var hits = AuditLogRedactor.RedactProperties(properties, options);

        Assert.Equal(1, hits);
        Assert.Contains("[MASKED]", properties["payload"], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", properties["payload"], StringComparison.Ordinal);
        Assert.Contains("Jane", properties["payload"], StringComparison.Ordinal);
    }

    [Fact]
    public void Default_keys_should_redact_connection_strings_in_action_arguments()
    {
        var options = new AuditRedactionOptions();
        AuditRedactionOptionsPostConfigure.Normalize(options);

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["parameters"] = "{\"input\":{\"ConnectionString\":\"Host=db;Database=t;Password=example\",\"TestBeforeSave\":true}}"
        };

        var hits = AuditLogRedactor.RedactProperties(properties, options);

        Assert.Equal(1, hits);
        Assert.DoesNotContain("Password=example", properties["parameters"], StringComparison.Ordinal);
        Assert.Contains("TestBeforeSave", properties["parameters"], StringComparison.Ordinal);
    }

    [Fact]
    public void RedactEntityChanges_should_use_configured_mask()
    {
        var options = new AuditRedactionOptions
        {
            Mask = "[MASKED]",
            SensitiveKeys = ["password"]
        };
        AuditRedactionOptionsPostConfigure.Normalize(options);

        var changes = new List<AuditLogWriteRequestEntityChange>
        {
            new()
            {
                EntityType = "Users.User",
                ChangeType = "Updated",
                PropertyChanges =
                [
                    new AuditLogWriteRequestPropertyChange
                    {
                        PropertyName = "PasswordHash",
                        OriginalValue = "old",
                        NewValue = "new"
                    }
                ]
            }
        };

        var hits = AuditLogRedactor.RedactEntityChanges(changes, options);

        Assert.Equal(1, hits);
        Assert.Equal("[MASKED]", changes[0].PropertyChanges[0].OriginalValue);
        Assert.Equal("[MASKED]", changes[0].PropertyChanges[0].NewValue);
    }

    [Fact]
    public void RedactProperties_should_redact_sensitive_form_encoded_values()
    {
        var options = new AuditRedactionOptions
        {
            Mask = "[MASKED]",
            SensitiveKeys = ["password"]
        };
        AuditRedactionOptionsPostConfigure.Normalize(options);
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["payload"] = "displayName=Jane&password=secret-value"
        };

        var hits = AuditLogRedactor.RedactProperties(properties, options);

        Assert.Equal(1, hits);
        Assert.Contains("displayName=Jane", properties["payload"], StringComparison.Ordinal);
        Assert.Contains("password=%5BMASKED%5D", properties["payload"], StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", properties["payload"], StringComparison.Ordinal);
    }

    [Fact]
    public void RedactPayload_should_identify_non_structured_payloads_as_unredactable()
    {
        var options = new AuditRedactionOptions();
        AuditRedactionOptionsPostConfigure.Normalize(options);

        var result = AuditLogRedactor.RedactPayload("unstructured secret payload", options);

        Assert.Equal(AuditPayloadRedactionOutcome.Unredactable, result.Outcome);
    }

}