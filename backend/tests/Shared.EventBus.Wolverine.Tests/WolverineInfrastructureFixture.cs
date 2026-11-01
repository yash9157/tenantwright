using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Shared.EventBus.Wolverine.Tests;

/// <summary>
/// One disposable RabbitMQ broker and one disposable PostgreSQL server for the Wolverine integration suite, both on
/// random loopback ports. Tests isolate themselves inside them: every test gets its own vhost (with chart-equivalent
/// per-principal permissions) and its own databases, so no test can observe another's messages or rows.
/// </summary>
public sealed class WolverineInfrastructureFixture : IAsyncLifetime, IDisposable
{
    /// <summary>The RabbitMQ release <c>charts/tenantwright-rabbitmq/values.yaml</c> pins.</summary>
    public const string RabbitImage = "rabbitmq:4.3.6-management";

    public const string PostgresImage = "postgres:17-alpine";

    private const string AdminUser = "wolverine-it-admin";
    private const string PostgresUser = "tenantwright";
    private const string PostgresDatabase = "tenantwright_wolverine";

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _postgresPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private IContainer? _rabbit;
    private IContainer? _postgres;
    private HttpClient? _management;

    /// <summary>The password every test principal authenticates with. Generated for this run only.</summary>
    public string PrincipalPassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public const string BrokerHost = "127.0.0.1";
    public int AmqpPort { get; private set; }
    public string AdminConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _rabbit = new ContainerBuilder(RabbitImage)
            .WithName($"wolverine-it-rabbit-{suffix}")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", AdminUser)
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", _adminPassword)
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithPortBinding(15672, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
            .Build();
        _postgres = new ContainerBuilder(PostgresImage)
            .WithName($"wolverine-it-postgres-{suffix}")
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", _postgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDatabase)
            .WithPortBinding(5432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("PostgreSQL init process complete; ready for start up"))
            .Build();

        await Task.WhenAll(_rabbit.StartAsync(), _postgres.StartAsync());

        AmqpPort = _rabbit.GetMappedPublicPort(5672);
        _management = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_rabbit.GetMappedPublicPort(15672)}/api/") };
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AdminUser}:{_adminPassword}")));

        AdminConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = _postgres.GetMappedPublicPort(5432),
            Database = PostgresDatabase,
            Username = PostgresUser,
            Password = _postgresPassword,
            Pooling = false
        }.ConnectionString;

        await WaitUntilAsync(async () => (await _management.GetAsync("overview")).IsSuccessStatusCode, "RabbitMQ management API");
        await WaitUntilAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
            return true;
        }, "PostgreSQL");

        foreach (var principal in WolverineTestTopology.Principals.Concat(RealTopologyPrincipals))
        {
            await PutAsync($"users/{principal}", new { password = PrincipalPassword, tags = string.Empty });
        }
    }

    /// <summary>Real topology principals the AppHost-style startup test composes.</summary>
    public static IReadOnlyList<string> RealTopologyPrincipals { get; } = ["tenantwright-tenant"];

    /// <summary>
    /// Creates an isolated vhost with the chart's permission model: every principal may configure and write
    /// <c>tenantwright-*</c> names and shared event exchanges, but may read only its own <c>{principal}-*</c> objects and the
    /// shared event exchanges.
    /// </summary>
    public async Task<WolverineBrokerTarget> CreateVirtualHostAsync()
    {
        var virtualHost = $"tenantwright-wolverine-{Guid.NewGuid():N}";
        await PutAsync($"vhosts/{Uri.EscapeDataString(virtualHost)}", new { });
        await PutAsync(
            $"permissions/{Uri.EscapeDataString(virtualHost)}/{AdminUser}",
            new { configure = ".*", write = ".*", read = ".*" });

        const string shared = "Shared.EventContracts.*|.*Eto";
        foreach (var principal in WolverineTestTopology.Principals.Concat(RealTopologyPrincipals))
        {
            await PutAsync(
                $"permissions/{Uri.EscapeDataString(virtualHost)}/{principal}",
                new
                {
                    configure = $"^(tenantwright-.*|{shared})$",
                    write = $"^(tenantwright-.*|{shared})$",
                    read = $"^({principal}(?:-|$).*|{shared})$"
                });
        }

        return new WolverineBrokerTarget(BrokerHost, AmqpPort, virtualHost, PrincipalPassword);
    }

    public BrokerAdmin Admin(WolverineBrokerTarget target) =>
        new(_management!, target.VirtualHost, BrokerHost, AmqpPort, AdminUser, _adminPassword);

    /// <summary>Creates an isolated database with the test schema and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"wolverine_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, Pooling = true }
            .ConnectionString;
        await using var dbContext = new WolverineTestDbContext(
            new DbContextOptionsBuilder<WolverineTestDbContext>().UseNpgsql(connectionString).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return connectionString;
    }

    public void Dispose() => _management?.Dispose();

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_rabbit is not null)
            await _rabbit.DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private async Task PutAsync(string path, object body)
    {
        using var response = await _management!.PutAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RabbitMQ management PUT {path} failed with {(int)response.StatusCode}.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, string what)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await probe())
                    return;
            }
            catch (Exception exception) when (exception is HttpRequestException or NpgsqlException or IOException)
            {
                last = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException($"{what} did not become ready in time ({last?.GetType().Name ?? "no error"}).");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WolverineInfrastructureSuite : ICollectionFixture<WolverineInfrastructureFixture>
{
    public const string Name = "wolverine-rabbitmq-postgres";
}

/// <summary>Broker-side observation through the management API, scoped to one test vhost.</summary>
public sealed class BrokerAdmin(
    HttpClient management,
    string virtualHost,
    string hostName,
    int port,
    string user,
    string password)
{
    public string VirtualHost => virtualHost;

    /// <summary>The queue's declared type, durability and arguments, or <see langword="null"/> when it does not exist.</summary>
    public async Task<QueueState?> QueueAsync(string queue)
    {
        using var response = await management.GetAsync(
            $"queues/{Uri.EscapeDataString(virtualHost)}/{Uri.EscapeDataString(queue)}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return new QueueState(
            root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("durable", out var durable) && durable.GetBoolean(),
            root.TryGetProperty("arguments", out var arguments) ? arguments.GetRawText() : "{}");
    }

    public async Task<IReadOnlyList<string>> BindingSourcesAsync(string queue)
    {
        using var response = await management.GetAsync(
            $"queues/{Uri.EscapeDataString(virtualHost)}/{Uri.EscapeDataString(queue)}/bindings");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(binding => binding.GetProperty("source").GetString() ?? string.Empty)
            .Where(source => source.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Waits until <paramref name="queue"/> holds <paramref name="expected"/> ready messages, read through AMQP so the
    /// count is immediate rather than the management API's sampled statistics. Meant for queues without consumers.
    /// </summary>
    public async Task<bool> WaitForReadyCountAsync(string queue, uint expected, TimeSpan timeout)
    {
        await using var client = Connect();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await client.ReadyCountAsync(queue) == expected)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return await client.ReadyCountAsync(queue) == expected;
    }

    /// <summary>A RabbitMQ.Client connection on the test vhost as the broker administrator.</summary>
    public RawBrokerClient Connect() => new(hostName, port, virtualHost, user, password);
}

/// <summary>The declared shape of a queue, as the broker recorded it.</summary>
public sealed record QueueState(string Type, bool Durable, string Arguments);
