using Microsoft.Extensions.Options;
using Shared.AuditLogs.Publishing;

namespace AuditLogs.Tests.TestDoubles;

/// <summary>
/// Builds a real <see cref="AuditLogEventSigner"/> for tests, backed by an in-memory options monitor
/// (no DI container needed). Used wherever a test constructs <c>AuditLogQueryAppService</c> or
/// <c>AuditLogSealService</c> directly and needs a concrete signer instance.
/// </summary>
internal static class AuditLogTestSignerFactory
{
    /// <summary>
    /// A signer with no configured signing key - <c>TrySign</c> is a no-op and <c>VerifyPersisted</c>
    /// returns null for any row (nothing to verify against). Sufficient for tests that don't exercise
    /// signature behavior directly.
    /// </summary>
    public static AuditLogEventSigner CreateDisabled()
    {
        return new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(new AuditLogSignatureOptions()));
    }

    /// <summary>
    /// A signer with a single active signing key, usable to actually sign/verify in tests that exercise
    /// sub-flow 1a/1b behavior end-to-end.
    /// </summary>
    public static AuditLogEventSigner CreateWithKey(string keyId, string base64Key)
    {
        var options = new AuditLogSignatureOptions
        {
            CurrentKeyId = keyId,
            Keys = new Dictionary<string, string>(StringComparer.Ordinal) { [keyId] = base64Key }
        };
        return new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(options));
    }

    public static AuditLogSignatureOptions CreateSignatureOptions(string keyId, string base64Key)
    {
        return new AuditLogSignatureOptions
        {
            CurrentKeyId = keyId,
            Keys = new Dictionary<string, string>(StringComparer.Ordinal) { [keyId] = base64Key }
        };
    }

    public static string CreateTestKeyBase64()
    {
        return Convert.ToBase64String(new byte[32]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32
        });
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;

    public TestOptionsMonitor(T value)
    {
        _value = value;
    }

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
