using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.EventBus.Messaging;
using Shared.Modularity.Context;
using Xunit;

namespace Shared.AuditLogs.Tests;

/// <summary>
/// A bus-free host (<c>Messaging:Provider=None</c>) must record audit through the structured log store: an audited
/// request or a direct audit write (such as a failed token request) never reaches the fail-closed command sender, so
/// it never throws and never logs at Error.
/// </summary>
public sealed class BusFreeAuditPathTests
{
    private static readonly Guid TenantId = Guid.Parse("4b0f6c3e-2f7d-4a52-9b67-0d0f3f4a8f11");
    private const string UserId = "0b7c2d9e-5a43-4c8e-8f0e-6d2b1a9c3e47";
    private const string ClientId = "tenantwright-tenant-admin";

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    public void Bus_free_host_should_route_the_auditing_store_and_direct_publishers_to_the_log_store(string? provider)
    {
        var services = ConfigureModule(provider);

        Assert.Equal(
            typeof(SimpleLogAuditingStore),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAuditingStore)).ImplementationType);
        Assert.Equal(
            typeof(LogOnlyAuditLogWriteRequestPublisher),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAuditLogWriteRequestPublisher)).ImplementationType);
    }

    [Fact]
    public void Real_provider_should_keep_the_durable_audit_path()
    {
        var services = ConfigureModule("MassTransit");

        Assert.Equal(
            typeof(EventBusAuditingStore),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAuditingStore)).ImplementationType);
        Assert.Equal(
            typeof(AuditLogWriteRequestPublisher),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAuditLogWriteRequestPublisher)).ImplementationType);
    }

    [Fact]
    public void Invalid_provider_should_fail_module_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigureModule("Kafka"));
    }

    [Fact]
    public async Task Bus_free_token_failure_audit_should_not_throw_and_should_not_log_errors_or_pii()
    {
        var logs = new RecordingLoggerProvider();
        using var provider = BuildProvider(logs);
        var publisher = provider.GetRequiredService<IAuditLogWriteRequestPublisher>();

        // The same shape Auth's TokenController writes for a denied token request.
        await publisher.PublishAsync(new AuditLogWriteRequest
        {
            EventId = Guid.CreateVersion7(),
            TenantId = TenantId,
            OccurredAtUtc = DateTime.UtcNow,
            Category = "auth.token.failure",
            UserId = UserId,
            HttpMethod = "POST",
            Path = "/connect/token",
            StatusCode = 401,
            Comments = ["user_inactive_or_locked"],
            Exceptions = [new AuditLogWriteRequestException { Type = "auth.token.failure", Message = "user_inactive_or_locked" }],
            RedactionApplied = true,
            RedactionVersion = 1,
            Actions =
            [
                new AuditLogWriteRequestAction
                {
                    Name = "auth.token.request.failure",
                    Result = "failure",
                    Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["grantType"] = "password",
                        ["clientId"] = ClientId,
                        ["reason"] = "user_inactive_or_locked"
                    }
                }
            ]
        });

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(typeof(SimpleLogAuditingStore).FullName, entry.Category);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Category=auth.token.failure", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Application=auth-service", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UserId, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientId, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantId.ToString("D"), entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(logs.Entries, log => log.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Bus_free_audit_publisher_should_still_reject_an_unredacted_request()
    {
        using var provider = BuildProvider(new RecordingLoggerProvider());
        var publisher = provider.GetRequiredService<IAuditLogWriteRequestPublisher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(new AuditLogWriteRequest
        {
            Category = "auth.token.failure",
            RedactionApplied = false,
            RedactionVersion = 0
        }));
    }

    [Fact]
    public async Task Bus_free_auditing_store_should_log_without_error()
    {
        var logs = new RecordingLoggerProvider();
        using var provider = BuildProvider(logs);

        await provider.GetRequiredService<IAuditingStore>().SaveAsync(new AuditLogInfo
        {
            ApplicationName = "auth-service",
            Category = "http.request",
            ExecutionTime = DateTime.UtcNow
        });

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Category=http.request", entry.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection ConfigureModule(string? provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditLogging:IsEnabled"] = "true",
                ["AuditLogging:ApplicationName"] = "auth-service",
                [TenantWrightMessagingProviderConfiguration.ProviderKey] = provider
            })
            .Build();
        var services = new ServiceCollection();
        new SharedAuditLogsModule().ConfigureServices(new ServiceConfigurationContext(services, configuration));
        return services;
    }

    private static ServiceProvider BuildProvider(RecordingLoggerProvider logs)
    {
        var services = ConfigureModule(provider: null);
        services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
                _entries.Add(entry);
        }

        private sealed class RecordingLogger(string category, RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
