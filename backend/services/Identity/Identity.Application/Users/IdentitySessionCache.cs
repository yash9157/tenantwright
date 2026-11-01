using System;
using System.Security.Cryptography;
using System.Text;

namespace Identity.Application.Users;

public static class IdentitySessionCache
{
    public static string ValidKey(Guid userId, string sessionId)
        => $"Identity:Session:Valid:{ComputeSha256Hex($"{userId:D}:{sessionId}")}";

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

public sealed class IdentitySessionValidityCacheItem
{
    public bool IsValid { get; set; }
}
