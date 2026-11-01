using System.Security.Cryptography;
using System.Text;

namespace Shared.MultiTenancy;

public static class TenantResolutionHash
{
    public static string HashTenantId(Guid? tenantId)
    {
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
        {
            return "host";
        }

        return HashString(tenantId.Value.ToString("D"));
    }

    public static string HashString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "none";
        }

        var bytes = Encoding.UTF8.GetBytes(input.Trim());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
