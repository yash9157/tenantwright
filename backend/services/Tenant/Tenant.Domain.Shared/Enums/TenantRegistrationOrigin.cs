namespace Tenant.Domain.Shared.Enums;

/// <summary>
/// Identifies how an initial-owner provisioning request entered the Tenant
/// boundary. Only PublicAuth records are eligible for automatic abandoned
/// registration cleanup.
/// </summary>
public enum TenantRegistrationOrigin
{
    SuperAdmin = 0,
    PublicAuth = 1
}
