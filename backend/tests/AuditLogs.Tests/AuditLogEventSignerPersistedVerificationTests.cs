using AuditLogs.Tests.TestDoubles;
using Shared.AuditLogs.Publishing;
using Xunit;

namespace AuditLogs.Tests;

/// <summary>
/// Sub-flow 1a: at-rest tamper-evidence. Verifies that <see cref="AuditLogEventSigner.VerifyPersisted"/>
/// correctly detects modification of any signed canonical field, and behaves gracefully for rows that
/// never had a signature captured.
/// </summary>
public class AuditLogEventSignerPersistedVerificationTests
{
    private const string KeyId = "key-1";

    [Fact]
    public void VerifyPersisted_should_return_true_for_untampered_row()
    {
        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var request = CreateRequest();
        signer.TrySign(request);

        var payload = AuditLogSignaturePayload.FromRequest(request);
        var verified = signer.VerifyPersisted(payload, request.SignatureKeyId, request.SignatureTimestampUtc, request.Signature);

        Assert.True(verified);
    }

    [Fact]
    public void VerifyPersisted_should_return_false_when_a_signed_field_is_modified()
    {
        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var request = CreateRequest();
        signer.TrySign(request);

        var payload = AuditLogSignaturePayload.FromRequest(request) with { UserId = "attacker" };
        var verified = signer.VerifyPersisted(payload, request.SignatureKeyId, request.SignatureTimestampUtc, request.Signature);

        Assert.False(verified);
    }

    [Fact]
    public void VerifyPersisted_should_return_false_when_signature_itself_is_modified()
    {
        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var request = CreateRequest();
        signer.TrySign(request);

        var payload = AuditLogSignaturePayload.FromRequest(request);
        var tamperedSignature = request.Signature!.Length > 4
            ? "00" + request.Signature![2..]
            : "00";
        var verified = signer.VerifyPersisted(payload, request.SignatureKeyId, request.SignatureTimestampUtc, tamperedSignature);

        Assert.False(verified);
    }

    [Fact]
    public void VerifyPersisted_should_return_null_when_row_was_never_signed()
    {
        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var request = CreateRequest();
        // No TrySign call - simulates a row written before sub-flow 1a shipped.

        var payload = AuditLogSignaturePayload.FromRequest(request);
        var verified = signer.VerifyPersisted(payload, signatureKeyId: null, signatureTimestampUtc: null, signature: null);

        Assert.Null(verified);
    }

    [Fact]
    public void VerifyPersisted_should_return_null_when_signing_key_has_been_rotated_out()
    {
        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var request = CreateRequest();
        signer.TrySign(request);

        // Simulate the key being rotated out of config entirely (not just superseded) by verifying
        // with a signer that has no keys configured.
        var signerWithoutKey = AuditLogTestSignerFactory.CreateDisabled();
        var payload = AuditLogSignaturePayload.FromRequest(request);
        var verified = signerWithoutKey.VerifyPersisted(payload, request.SignatureKeyId, request.SignatureTimestampUtc, request.Signature);

        Assert.Null(verified);
    }

    private static AuditLogWriteRequest CreateRequest()
    {
        return new AuditLogWriteRequest
        {
            EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            OccurredAtUtc = DateTime.SpecifyKind(new DateTime(2026, 3, 6, 20, 0, 0), DateTimeKind.Utc),
            SourceService = "identity",
            Category = "http",
            UserId = "user-1",
            CorrelationId = "corr-1",
            Path = "/api/users",
            PayloadBytes = 128,
            RedactionApplied = false,
            RedactionVersion = 0
        };
    }
}
