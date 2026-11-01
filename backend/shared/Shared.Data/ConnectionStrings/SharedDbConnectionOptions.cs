using System;
using System.Collections.Generic;
using Shared.Data.Database;

namespace Shared.Data.ConnectionStrings;

public class SharedDbConnectionOptions
{
    public ConnectionStrings ConnectionStrings { get; set; }

    public SharedDatabaseInfoDictionary Databases { get; set; }

    public SharedDbConnectionOptions()
    {
        ConnectionStrings = new ConnectionStrings();
        Databases = new SharedDatabaseInfoDictionary();
    }

    public string? GetConnectionStringOrNull(
        string connectionStringName,
        bool fallbackToDatabaseMappings = true,
        bool fallbackToDefault = true)
    {
        var connectionString = ConnectionStrings.GetValueOrDefault(connectionStringName);
        if (!string.IsNullOrEmpty(connectionString))
        {
            return connectionString;
        }

        if (fallbackToDatabaseMappings)
        {
            var database = Databases.GetMappedDatabaseOrNull(connectionStringName);
            if (database != null)
            {
                connectionString = ConnectionStrings.GetValueOrDefault(database.DatabaseName);
                if (!string.IsNullOrEmpty(connectionString))
                {
                    return connectionString;
                }
            }
        }

        if (fallbackToDefault)
        {
            connectionString = ConnectionStrings.Default;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }
        }

        return null;
    }
}
