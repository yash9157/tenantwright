using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Xunit;

namespace Notifications.Tests;

/// <summary>One disposable server; every test creates its own disposable database inside it.</summary>
public sealed class NotificationPostgresFixture : IAsyncLifetime
{
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private IContainer? _container;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:17-alpine")
            .WithName($"tenantwright-notifications-test-{Guid.NewGuid():N}")
            .WithEnvironment("POSTGRES_USER", "tenantwright")
            .WithEnvironment("POSTGRES_PASSWORD", _password)
            .WithEnvironment("POSTGRES_DB", "tenantwright_notifications_test")
            .WithPortBinding(5432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("PostgreSQL init process complete; ready for start up"))
            .Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _container.StartAsync(timeout.Token);
        _connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = _container.GetMappedPublicPort(5432),
            Database = "tenantwright_notifications_test",
            Username = "tenantwright",
            Password = _password,
            Pooling = false
        }.ConnectionString;
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var databaseName = $"notifications_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.OpenAsync(timeout.Token);
        // The identifier is generated exclusively from a fixed prefix and a GUID, not caller input.
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync(timeout.Token);
        return new NpgsqlConnectionStringBuilder(_connectionString) { Database = databaseName }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NotificationPostgresSuite : ICollectionFixture<NotificationPostgresFixture>
{
    public const string Name = "notifications-postgres";
}
