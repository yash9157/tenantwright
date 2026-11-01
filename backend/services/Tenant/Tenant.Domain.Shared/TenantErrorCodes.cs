namespace Tenant.Domain.Shared;

public static class TenantErrorCodes
{
    public const string TenantContextRequired = "tenant_context_required";

    /// <summary>The database name is not a service database a tenant may move to a dedicated database.</summary>
    public const string DedicatedDatabaseNameNotSupported = "tenant_dedicated_database_name_not_supported";

    /// <summary>The supplied value is not a well-formed PostgreSQL connection string with a host and database.</summary>
    public const string DedicatedDatabaseConnectionStringInvalid = "tenant_dedicated_database_connection_string_invalid";

    /// <summary>The target is a shared service database or is already assigned to another dedicated database.</summary>
    public const string DedicatedDatabaseTargetInUse = "tenant_dedicated_database_target_in_use";

    /// <summary>Changing or removing an existing assignment requires acknowledging that no data is migrated.</summary>
    public const string DedicatedDatabaseDataMigrationNotAcknowledged = "tenant_dedicated_database_data_migration_not_acknowledged";

    /// <summary>The tenant has no dedicated database for the name.</summary>
    public const string DedicatedDatabaseNotAssigned = "tenant_dedicated_database_not_assigned";
}
