using Shared;

namespace Tenant.Application.Tenants;

// Derives from SharedConflictException so GlobalExceptionHandlingMiddleware maps
// it to HTTP 409 for every collision path (public create, public rename, internal
// create, restore) without per-controller handling.
public class TenantNameAlreadyExistsException : SharedConflictException
{
    public TenantNameAlreadyExistsException(string name)
        : base($"Tenant name already exists: {name}")
    {
    }
}
