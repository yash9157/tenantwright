namespace ApiGateway;

public sealed class GatewayLimitsOptions
{
    public const string SectionName = "GatewayLimits";
    public const int RequiredMaxAuthorizationHeaderBytes = 8 * 1024;
    public const int RequiredMaxRequestHeadersTotalSize = 32 * 1024;

    public int MaxAuthorizationHeaderBytes { get; set; } = RequiredMaxAuthorizationHeaderBytes;

    public int MaxRequestHeadersTotalSize { get; set; } = RequiredMaxRequestHeadersTotalSize;

    public static bool IsValid(GatewayLimitsOptions options)
        => options.MaxAuthorizationHeaderBytes == RequiredMaxAuthorizationHeaderBytes
           && options.MaxRequestHeadersTotalSize == RequiredMaxRequestHeadersTotalSize;

    public static void EnsureValid(GatewayLimitsOptions options)
    {
        if (IsValid(options))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{SectionName}:MaxAuthorizationHeaderBytes must be exactly {RequiredMaxAuthorizationHeaderBytes}, and " +
            $"{SectionName}:MaxRequestHeadersTotalSize must be exactly {RequiredMaxRequestHeadersTotalSize}.");
    }
}
