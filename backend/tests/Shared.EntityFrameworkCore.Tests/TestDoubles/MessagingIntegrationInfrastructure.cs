using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shared.EntityFrameworkCore;
using Shared.EventContracts;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests.TestDoubles;

/// <summary>
/// Shared infrastructure for the live messaging integration suites in this project.
/// </summary>
/// <remarks>
/// Shared by the trusted MassTransit production-path suite and the neutral PostgreSQL outbox/inbox suite. Both suites
/// start their own disposable containers; nothing here is shared between test runs, because a shared broker or
/// database would let one test observe another's messages.
/// </remarks>
internal static class MessagingIntegrationDefaults
{
    public const string RabbitImage = "rabbitmq:3.13-management";
    public const string PostgresImage = "postgres:17-alpine";

    /// <summary>
    /// Pinned to match the repository's Valkey version in <c>charts/tenantwright-valkey/values.yaml</c>. A disposable instance is required because
    /// <c>DistributedLockingModule</c> supports only Valkey and refuses to configure without a connection string, even
    /// in Development. The developer's compose Valkey is deliberately never used: a shared lock store would let two
    /// runs contend on the same keys.
    /// </summary>
    public const string ValkeyImage = "valkey/valkey:9.1.1";

    /// <summary>Container name prefix, so an abandoned container is recognisable and greppable.</summary>
    public const string ContainerPrefix = "tenantwright-mt-phase3";
}

/// <summary>
/// Opt-in gate for the live messaging suites, matching the existing infrastructure-test convention so the default
/// suite never depends on Docker.
/// </summary>
internal sealed class MessagingInfrastructureFactAttribute : FactAttribute
{
    public MessagingInfrastructureFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TENANTWRIGHT_RUN_MASSTRANSIT_RABBITMQ_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Requires TENANTWRIGHT_RUN_MASSTRANSIT_RABBITMQ_TESTS=1, Docker, and exactly one approved MassTransit license interface.";
        }
    }
}

/// <summary>A write made by a producer inside the same transaction as its published message.</summary>
internal sealed class ProducerWrite(Guid messageId)
{
    public Guid MessageId { get; private set; } = messageId;
}

/// <summary>An effect written by a consumer, used to prove inbox-controlled exactly-once handling.</summary>
internal sealed class ConsumerEffect(Guid messageId)
{
    public Guid MessageId { get; private set; } = messageId;
}

/// <summary>
/// Disposable RabbitMQ and PostgreSQL containers on random loopback ports with random names, so concurrent runs and
/// the developer's compose stack cannot interfere with each other.
/// </summary>
internal sealed class DisposableMessagingInfrastructure : IAsyncDisposable
{
    private readonly string _rabbitContainer;
    private readonly string _postgresContainer;
    private readonly string? _valkeyContainer;

    private DisposableMessagingInfrastructure(
        string rabbitContainer,
        string postgresContainer,
        string? valkeyContainer,
        string rabbitPassword,
        int rabbitPort,
        string postgresConnectionString,
        string? valkeyConfiguration)
    {
        _rabbitContainer = rabbitContainer;
        _postgresContainer = postgresContainer;
        _valkeyContainer = valkeyContainer;
        RabbitPassword = rabbitPassword;
        RabbitPort = rabbitPort;
        PostgresConnectionString = postgresConnectionString;
        ValkeyConfiguration = valkeyConfiguration;
    }

    public string RabbitPassword { get; }
    public int RabbitPort { get; }
    public string PostgresConnectionString { get; }

    /// <summary>
    /// Creates an additional database in the disposable PostgreSQL instance. Separate service hosts must not share
    /// MassTransit's bus-outbox tables: every delivery worker is entitled to claim rows from its own database.
    /// </summary>
    public async Task<string> CreatePostgresDatabaseAsync()
    {
        var databaseName = $"tenantwright_{Guid.NewGuid():N}";
        var result = await RunDockerAsync(
            ["exec", _postgresContainer, "createdb", "--username", "tenantwright", databaseName],
            TimeSpan.FromSeconds(30));

        if (result.ExitCode != 0)
            throw new InvalidOperationException("Could not create an isolated PostgreSQL database for a messaging host.");

        var connectionString = new NpgsqlConnectionStringBuilder(PostgresConnectionString)
        {
            Database = databaseName
        };
        return connectionString.ConnectionString;
    }

    /// <summary>
    /// The isolated vhost. <c>MessagingOptionsValidator</c> refuses <c>'/'</c>, so the broker is created with a named
    /// vhost per run rather than the default.
    /// </summary>
    public string RabbitVirtualHost { get; private set; } = "/";

    /// <summary>
    /// Creates one RabbitMQ user per principal on the isolated vhost.
    /// </summary>
    /// <remarks>
    /// <c>MessagingOptionsValidator</c> requires the connection username to equal the producer principal exactly, so a
    /// broker shared by a producer and a consumer host needs one user per principal. That rule is production custody and
    /// is satisfied rather than relaxed.
    /// </remarks>
    public async Task ProvisionPrincipalsAsync(IEnumerable<string> principals)
    {
        foreach (var principal in principals)
        {
            var add = await RunDockerAsync(
                ["exec", _rabbitContainer, "rabbitmqctl", "add_user", principal, RabbitPassword],
                TimeSpan.FromSeconds(30));
            if (add.ExitCode != 0)
            {
                // The password is generated for this container only and is already known to the test process, so
                // echoing broker diagnostics here discloses nothing that is not already in memory.
                throw new InvalidOperationException(
                    $"Could not create broker user '{principal}'. exit={add.ExitCode} " +
                    $"stdout={add.StandardOutput.Trim()} stderr={add.StandardError.Trim()}");
            }

            var permissions = await RunDockerAsync(
                [
                    "exec", _rabbitContainer, "rabbitmqctl", "set_permissions",
                    "--vhost", RabbitVirtualHost, principal, ".*", ".*", ".*"
                ],
                TimeSpan.FromSeconds(30));
            if (permissions.ExitCode != 0)
                throw new InvalidOperationException($"Could not grant broker permissions to '{principal}'.");
        }
    }

    /// <summary>
    /// The StackExchange.Redis configuration string for the disposable Valkey, or <see langword="null"/> when the
    /// caller did not ask for one. Only <c>DistributedLocking:Valkey:Configuration</c> should be pointed at it; caching
    /// stays on the in-memory path.
    /// </summary>
    public string? ValkeyConfiguration { get; }

    /// <summary>
    /// Queue names and depths on the isolated vhost. Used to tell a message that was never routed from one that landed
    /// in the endpoint's <c>_skipped</c> or <c>_error</c> queue, which a consume observer cannot see.
    /// </summary>
    public async Task<string> ListQueuesAsync()
    {
        var result = await RunDockerAsync(
            ["exec", _rabbitContainer, "rabbitmqctl", "list_queues", "--vhost", RabbitVirtualHost, "name", "messages"],
            TimeSpan.FromSeconds(30));

        return result.ExitCode == 0
            ? string.Join(
                " | ",
                result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith("Timeout", StringComparison.Ordinal)
                        && !line.StartsWith("Listing", StringComparison.Ordinal)))
            : $"list_queues-exit={result.ExitCode}";
    }

    /// <summary>
    /// Per-queue depths on the isolated vhost, so a specific endpoint's input, error and skipped queues can be
    /// baselined individually rather than aggregated across both permission-cache endpoints.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> QueueDepthsAsync()
    {
        var result = await RunDockerAsync(
            ["exec", _rabbitContainer, "rabbitmqctl", "list_queues", "--vhost", RabbitVirtualHost, "name", "messages"],
            TimeSpan.FromSeconds(30));

        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        if (result.ExitCode != 0)
        {
            return depths;
        }

        foreach (var line in result.StandardOutput.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out var messages))
            {
                depths[parts[0]] = messages;
            }
        }

        return depths;
    }

    /// <summary>Bindings on the isolated vhost, used to prove the production endpoint binding exists.</summary>
    public async Task<string> ListBindingsAsync()
    {
        var result = await RunDockerAsync(
            [
                "exec", _rabbitContainer, "rabbitmqctl", "list_bindings", "--vhost", RabbitVirtualHost,
                "source_name", "destination_name"
            ],
            TimeSpan.FromSeconds(30));

        return result.ExitCode == 0 ? result.StandardOutput : $"list_bindings-exit={result.ExitCode}";
    }

    public async Task KillRabbitAsync()
    {
        var result = await RunDockerAsync(
            ["kill", "--signal=KILL", _rabbitContainer],
            TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The disposable RabbitMQ container could not be stopped.");
    }

    public async Task StartRabbitAsync()
    {
        var result = await RunDockerAsync(["start", _rabbitContainer], TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The disposable RabbitMQ container could not be restarted.");
        await WaitForHealthyAsync(_rabbitContainer);
    }

    /// <param name="includeValkey">
    /// Adds a disposable Valkey. Required only by suites that compose the real module graph, because
    /// <c>DistributedLockingModule</c> will not configure without a Valkey connection string.
    /// </param>
    public static async Task<DisposableMessagingInfrastructure> StartAsync(bool includeValkey = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var rabbitName = $"{MessagingIntegrationDefaults.ContainerPrefix}-rabbit-{suffix}";
        var postgresName = $"{MessagingIntegrationDefaults.ContainerPrefix}-postgres-{suffix}";
        var valkeyName = $"{MessagingIntegrationDefaults.ContainerPrefix}-valkey-{suffix}";
        var rabbitPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var postgresPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        var ports = DistinctLoopbackPorts(includeValkey ? 3 : 2);
        var rabbitPort = ports[0];
        var postgresPort = ports[1];
        var valkeyPort = includeValkey ? ports[2] : 0;

        // An isolated vhost per run: MessagingOptionsValidator refuses '/', and two concurrent runs must not share
        // exchanges or queues.
        var virtualHost = $"tenantwright-test-{suffix}";

        await StartContainerAsync(
            rabbitName,
            [
                "--hostname", rabbitName,
                "--publish", $"127.0.0.1:{rabbitPort}:5672",
                "--env", "RABBITMQ_DEFAULT_USER=tenantwright",
                "--env", $"RABBITMQ_DEFAULT_PASS={rabbitPassword}",
                "--env", $"RABBITMQ_DEFAULT_VHOST={virtualHost}",

                // check_running rather than ping: ping only proves the Erlang node answers, while the broker's own
                // application may still be booting. A container reported healthy on ping alone accepted no
                // rabbitmqctl management commands.
                "--health-cmd", "rabbitmq-diagnostics -q check_running",

                // The interval is 3s, not 1s, and that is load-bearing. Every rabbitmq-diagnostics invocation starts its
                // own Erlang CLI node, which reads and writes /var/lib/rabbitmq/.erlang.cookie. RabbitMQ needs roughly
                // 18 seconds to boot here, so a 1-second interval spawned about eighteen CLI nodes racing the broker's
                // own cookie initialization; the broker then failed prelaunch with
                // "Error when reading /var/lib/rabbitmq/.erlang.cookie: eacces" and the container exited. The probe was
                // breaking the thing it measured. A start period keeps those early boot probes from counting as
                // failures, and 3s x 40 retries preserves the same two-minute bound as before.
                "--health-start-period", "20s",
                "--health-interval", "3s", "--health-timeout", "10s", "--health-retries", "40",
                MessagingIntegrationDefaults.RabbitImage
            ]);

        var started = new List<string> { rabbitName };
        try
        {
            await StartContainerAsync(
                postgresName,
                [
                    "--publish", $"127.0.0.1:{postgresPort}:5432",
                    "--env", "POSTGRES_USER=tenantwright",
                    "--env", $"POSTGRES_PASSWORD={postgresPassword}",
                    "--env", "POSTGRES_DB=tenantwright_phase3",
                    "--health-cmd", "pg_isready -U tenantwright -d tenantwright_phase3",
                    "--health-interval", "1s", "--health-timeout", "5s", "--health-retries", "60",
                    MessagingIntegrationDefaults.PostgresImage
                ]);
            started.Add(postgresName);

            string? valkeyConfiguration = null;
            if (includeValkey)
            {
                await StartContainerAsync(
                    valkeyName,
                    [
                        "--publish", $"127.0.0.1:{valkeyPort}:6379",
                        "--health-cmd", "valkey-cli ping",
                        "--health-interval", "1s", "--health-timeout", "5s", "--health-retries", "60",
                        MessagingIntegrationDefaults.ValkeyImage
                    ]);
                started.Add(valkeyName);

                // Loopback-only and ephemeral, so no password. abortConnect=False keeps a transient startup race from
                // failing the whole composition.
                valkeyConfiguration =
                    $"127.0.0.1:{valkeyPort},abortConnect=False,connectRetry=3,connectTimeout=5000,syncTimeout=5000";
            }

            return new DisposableMessagingInfrastructure(
                rabbitName,
                postgresName,
                includeValkey ? valkeyName : null,
                rabbitPassword,
                rabbitPort,
                $"Host=127.0.0.1;Port={postgresPort};Database=tenantwright_phase3;Username=tenantwright;Password={postgresPassword};Pooling=false",
                valkeyConfiguration)
            {
                RabbitVirtualHost = virtualHost
            };
        }
        catch
        {
            // Every container started so far is removed, so a partial start never leaks one.
            foreach (var name in started)
                await RemoveAsync(name);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_valkeyContainer is not null)
            await RemoveAsync(_valkeyContainer);
        await RemoveAsync(_postgresContainer);
        await RemoveAsync(_rabbitContainer);
    }

    /// <summary>Allocates the requested number of distinct free loopback ports.</summary>
    private static int[] DistinctLoopbackPorts(int count)
    {
        var ports = new List<int>(count);
        while (ports.Count < count)
        {
            var candidate = AvailableLoopbackPort();
            if (!ports.Contains(candidate)) ports.Add(candidate);
        }
        return [.. ports];
    }

    /// <summary>Number of times a container that exits during startup is recreated before giving up.</summary>
    private const int StartAttempts = 3;

    /// <summary>
    /// Starts a container and waits for its health check, recreating it if it exits during startup.
    /// </summary>
    /// <remarks>
    /// The retry exists because an exited container is in a terminal state: waiting longer can never make it healthy, so
    /// the only useful response is a fresh container. Observed cause on this host was RabbitMQ failing to read its own
    /// Erlang cookie (<c>eacces</c>) on a container with no volume mount and no user override, which the fixture cannot
    /// influence. Timeouts are deliberately unchanged, and a container that is merely slow is not retried — only one
    /// that has terminated. The final failure still reports the container's real state, probe output and logs.
    /// </remarks>
    private static async Task StartContainerAsync(string name, IReadOnlyList<string> options)
    {
        for (var attempt = 1; ; attempt++)
        {
            var arguments = new List<string> { "run", "--detach", "--name", name };
            arguments.AddRange(options);
            var result = await RunDockerAsync(arguments, TimeSpan.FromMinutes(2));
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"The disposable container '{name}' could not be started.");

            TimeoutException? readinessFailure = null;
            try
            {
                await WaitForHealthyAsync(name);
                return;
            }
            catch (TimeoutException timeout)
            {
                readinessFailure = timeout;
            }
            catch
            {
                await RemoveAsync(name);
                throw;
            }

            // A container that has terminated will never become healthy, so recreate it. One that is merely slow is not
            // retried: that would be hiding a real problem behind a longer wait.
            var exited = await HasExitedAsync(name);
            await RemoveAsync(name);

            if (!exited || attempt >= StartAttempts)
            {
                throw readinessFailure;
            }
        }
    }

    /// <summary>True when the container has reached a terminal state, so recreating it is the only way forward.</summary>
    private static async Task<bool> HasExitedAsync(string name)
    {
        var inspect = await RunDockerAsync(
            ["inspect", "--format={{.State.Status}}", name],
            TimeSpan.FromSeconds(10));

        return inspect.ExitCode == 0
            && inspect.StandardOutput.Trim() is "exited" or "dead";
    }

    /// <summary>
    /// Waits for a container's own health check, reporting what it observed when it gives up.
    /// </summary>
    /// <remarks>
    /// Cancelling the inner <c>Task.Delay</c> previously made the terminal <see cref="TimeoutException"/> unreachable:
    /// a readiness failure surfaced as an opaque <c>TaskCanceledException</c> with no container state, health status, or
    /// probe output, which is why an earlier failure here could not be diagnosed. The same defect existed in the
    /// Shared.EventBus live fixture and was corrected the same way.
    ///
    /// The bound is deliberately unchanged. This reports the failure truthfully rather than hiding it behind a longer
    /// wait.
    /// </remarks>
    private static async Task WaitForHealthyAsync(string name)
    {
        var deadline = TimeSpan.FromMinutes(2);
        var stopwatch = Stopwatch.StartNew();
        var lastStatus = "not-observed";
        while (stopwatch.Elapsed < deadline)
        {
            var inspect = await RunDockerAsync(
                ["inspect", "--format={{.State.Status}}|{{.State.Health.Status}}|{{.State.Health.FailingStreak}}", name],
                TimeSpan.FromSeconds(10));
            lastStatus = inspect.ExitCode == 0 ? inspect.StandardOutput.Trim() : $"inspect-exit={inspect.ExitCode}";
            if (lastStatus.Contains("|healthy|", StringComparison.Ordinal))
                return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        // Diagnostics from the container's own probe plus its final log lines. Neither echoes a credential: the
        // generated passwords are passed as environment variables and never appear in a health command, its output, or
        // RabbitMQ/PostgreSQL startup logging.
        //
        // The log tail matters because the caller force-removes the container on failure, which previously destroyed
        // the only evidence of *why* readiness failed. An observed failure showed `exited|unhealthy|2`, meaning the
        // container terminated rather than started slowly — a distinction the state alone cannot explain.
        var probe = await RunDockerAsync(
            ["inspect", "--format={{range .State.Health.Log}}exit={{.ExitCode}} out={{.Output}}{{end}}", name],
            TimeSpan.FromSeconds(10));
        var exitCode = await RunDockerAsync(
            ["inspect", "--format={{.State.ExitCode}}|{{.State.OOMKilled}}|{{.State.Error}}", name],
            TimeSpan.FromSeconds(10));
        var logs = await RunDockerAsync(["logs", "--tail", "25", name], TimeSpan.FromSeconds(20));

        throw new TimeoutException(
            $"The disposable container '{name}' did not become healthy within {deadline}. " +
            $"Last observed state: {lastStatus}. " +
            $"Exit code / OOM-killed / error: {exitCode.StandardOutput.Trim()}. " +
            $"Health probe log: {probe.StandardOutput.Trim()}. " +
            $"Container log tail: {logs.StandardOutput.Trim()}");
    }

    private static int AvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RemoveAsync(string name)
    {
        // --volumes matters: the RabbitMQ, PostgreSQL and Valkey images all declare VOLUME, so every `docker run`
        // creates an anonymous volume. Removing the container without it left one behind per container per test run,
        // which accumulates without bound on a developer machine.
        _ = await RunDockerAsync(["rm", "--force", "--volumes", name], TimeSpan.FromSeconds(30));
    }

    private static async Task<DockerResult> RunDockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException("Docker could not be started for a disposable messaging container.");
        using var cancellation = new CancellationTokenSource(timeout);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var standardError = process.StandardError.ReadToEndAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);
        return new DockerResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record DockerResult(int ExitCode, string StandardOutput, string StandardError);
}

/// <summary>
/// Observes what the real permission-cache receive endpoint actually consumed.
/// </summary>
/// <remarks>
/// A consume observer is used deliberately instead of adding a test consumer or a second queue bound to the same
/// exchange. Either of those would compete with the production
/// <c>{principal}-permission-cache-v1</c> endpoint for messages and could pass while the production endpoint was
/// broken. This attaches to the existing endpoint's consume pipeline and changes no topology.
/// </remarks>
internal sealed class PermissionCacheConsumeObserver : IConsumeObserver
{
    private readonly ConcurrentDictionary<string, int> _consumed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Uri> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid?> _tenants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _headers = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Exception> _faults = [];

    public IReadOnlyCollection<Exception> Faults => _faults;

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

    private readonly ConcurrentDictionary<string, List<string>> _receipts = new(StringComparer.Ordinal);

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
    {
        if (context.Message is PermissionGrantsChangedTenantEto message)
        {
            _consumed.AddOrUpdate(message.ProviderKey, 1, (_, count) => count + 1);
            _endpoints[message.ProviderKey] = context.ReceiveContext.InputAddress;
            _tenants[message.ProviderKey] = message.TenantId;
            _headers[message.ProviderKey] = context.Headers.GetAll()
                .ToDictionary(header => header.Key, header => (object?)header.Value, StringComparer.OrdinalIgnoreCase);

            // The identity each delivery actually arrived with, which is what inbox deduplication keys on.
            var receipt =
                $"messageId={context.MessageId?.ToString("D") ?? "<null>"} " +
                $"endpoint={context.ReceiveContext.InputAddress?.AbsolutePath ?? "<null>"} " +
                $"redelivery={context.GetRedeliveryCount()}";

            _receipts.AddOrUpdate(
                message.ProviderKey,
                _ => [receipt],
                (_, list) =>
                {
                    lock (list)
                    {
                        list.Add(receipt);
                    }

                    return list;
                });
        }

        return Task.CompletedTask;
    }

    /// <summary>Every consume receipt recorded for a provider key, in arrival order.</summary>
    public IReadOnlyList<string> ReceiptsFor(string providerKey)
    {
        if (!_receipts.TryGetValue(providerKey, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.ToList();
        }
    }

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        _faults.Add(exception);
        return Task.CompletedTask;
    }

    public int CountFor(string providerKey) => _consumed.TryGetValue(providerKey, out var count) ? count : 0;

    public Uri? EndpointFor(string providerKey) => _endpoints.TryGetValue(providerKey, out var uri) ? uri : null;

    public Guid? TenantFor(string providerKey) => _tenants.TryGetValue(providerKey, out var tenant) ? tenant : null;

    public IReadOnlyDictionary<string, object?> HeadersFor(string providerKey)
        => _headers.TryGetValue(providerKey, out var headers)
            ? headers
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Polls until the endpoint has consumed <paramref name="expected"/> messages, or the timeout elapses.</summary>
    public async Task<bool> WaitForCountAsync(string providerKey, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CountFor(providerKey) >= expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return CountFor(providerKey) >= expected;
    }

    /// <summary>
    /// Waits out a settling window and asserts nothing arrived. Used for the negative proofs, where the only evidence
    /// is the continued absence of a delivery.
    /// </summary>
    public async Task<bool> StaysAtAsync(string providerKey, int expected, TimeSpan settle)
    {
        await Task.Delay(settle);
        return CountFor(providerKey) == expected;
    }
}
