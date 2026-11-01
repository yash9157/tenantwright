using System.Security.Cryptography;
using System.Text;

namespace Identity.Domain.Shared.ApplicationUsers;

public static class IdentitySystemRoleIds
{
    public static readonly Guid HostAdmin = Guid.Parse("66d1d966-cb76-5f42-a8a4-dc8317098154");

    public static Guid TenantAdmin(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        var source = Encoding.UTF8.GetBytes($"tenantwright:identity:tenant-admin:{tenantId:D}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(source, hash);

        // Use RFC 4122 variant/version bits so the derived value remains a valid,
        // stable namespace-based identifier rather than an opaque hash fragment.
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }
}
