using System;

namespace Identity.Data;

public sealed class IdentityBootstrapSeedOptions
{
    public bool Enabled { get; set; }

    public IdentityBootstrapHostAdminOptions HostAdmin { get; set; } = new();

    public IdentityBootstrapTenantAdminOptions TenantAdmin { get; set; } = new();
}

public sealed class IdentityBootstrapHostAdminOptions
{
    public bool Enabled { get; set; } = true;

    public Guid TenantId { get; set; } = Guid.Empty;

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    public bool ResetPasswordOnSeed { get; set; }
}

public sealed class IdentityBootstrapTenantAdminOptions
{
    public bool Enabled { get; set; }

    public Guid TenantId { get; set; }

    public string? TenantName { get; set; }

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    public bool ResetPasswordOnSeed { get; set; }
}
