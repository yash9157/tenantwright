namespace AuditLogs.Domain.Shared.Auditing;

public static class AuditLogSealConsts
{
    public const int MaxHashLength = 128;
    public const int MaxSignatureLength = 512;
    public const int MaxSignatureKeyIdLength = 128;

    /// <summary>
    /// Genesis value used as the "previous seal hash" for the very first seal in the chain
    /// (i.e. when no earlier seal exists yet).
    /// </summary>
    public const string GenesisHash = "GENESIS";
}
