namespace Shared.Data.ConnectionStrings;

public interface ITenantConnectionStringProtector
{
    string Protect(Guid tenantId, string connectionName, string plaintext);

    string Unprotect(Guid tenantId, string connectionName, string storedValue);

    bool IsProtected(string? storedValue);
}
