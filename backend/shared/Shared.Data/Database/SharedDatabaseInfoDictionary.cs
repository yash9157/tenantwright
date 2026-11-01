using System;
using System.Collections.Generic;

namespace Shared.Data.Database;

public class SharedDatabaseInfoDictionary : Dictionary<string, SharedDatabaseInfo>
{
    private Dictionary<string, SharedDatabaseInfo> ConnectionIndex { get; set; }

    public SharedDatabaseInfoDictionary()
    {
        ConnectionIndex = new Dictionary<string, SharedDatabaseInfo>();
    }

    public SharedDatabaseInfo? GetMappedDatabaseOrNull(string connectionStringName)
    {
        return ConnectionIndex.GetValueOrDefault(connectionStringName);
    }

    public SharedDatabaseInfoDictionary Configure(string databaseName, Action<SharedDatabaseInfo> configureAction)
    {
        if (!ContainsKey(databaseName))
        {
            this[databaseName] = new SharedDatabaseInfo(databaseName);
        }

        configureAction(this[databaseName]);

        return this;
    }

    /// <summary>
    /// This method should be called if this dictionary changes.
    /// It refreshes indexes for quick access to the connection informations.
    /// </summary>
    public void RefreshIndexes()
    {
        ConnectionIndex = new Dictionary<string, SharedDatabaseInfo>();

        foreach (var databaseInfo in Values)
        {
            foreach (var mappedConnection in databaseInfo.MappedConnections)
            {
                if (ConnectionIndex.ContainsKey(mappedConnection))
                {
                    throw new InvalidOperationException(
                        $"A connection name can not map to multiple databases: {mappedConnection}."
                    );
                }

                ConnectionIndex[mappedConnection] = databaseInfo;
            }
        }
    }
}
