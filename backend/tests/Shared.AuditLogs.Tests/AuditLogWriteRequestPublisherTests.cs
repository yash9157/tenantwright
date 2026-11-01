using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs;
using Shared.AuditLogs.Auditing;
using Shared.EventContracts;
using Shared.AuditLogs.Publishing;
using Shared.EventBus.Messaging;
using Shared.Modularity.Context;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.AuditLogs.Tests;

public class AuditLogWriteRequestPublisherTests
{
    [Fact]
    public async Task PublishAsync_should_publish_normalized_audit_log_request()
    {
        var bus = new CapturingMessageSender();
        var publisher = new AuditLogWriteRequestPublisher(
            bus,
            Microsoft.Extensions.Options.Options.Create(new SharedAuditingOptions
            {
                ApplicationName = "identity-service"
            }),
            new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(new AuditLogSignatureOptions())),
            new RecordingCurrentTenant());

        await publisher.PublishAsync(
            new AuditLogWriteRequest
            {
                Category = "http.request",
                RedactionApplied = true,
                RedactionVersion = 1,
                Actions =
                [
                    new AuditLogWriteRequestAction
                    {
                        Name = "http.request",
                        Result = "success"
                    }
                ]
            });

        var published = Assert.IsType<AuditLogWriteRequestedHostEto>(bus.LastEventData);
        Assert.Equal(typeof(AuditLogWriteRequestedHostEto), bus.LastEventType);
        Assert.Equal("identity-service", published.SourceService);
        Assert.Equal("http.request", published.Category);
        Assert.True(published.RedactionApplied);
        Assert.Equal(1, published.RedactionVersion);
        Assert.True(bus.LastWasCommand);
        Assert.NotEqual(Guid.Empty, published.EventId);
    }

    [Fact]
    public async Task PublishAsync_should_attach_signature_metadata_when_key_is_configured()
    {
        var bus = new CapturingMessageSender();
        var signingOptions = new AuditLogSignatureOptions
        {
            CurrentKeyId = "audit-key-1",
            Keys =
            {
                ["audit-key-1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        };
        var publisher = new AuditLogWriteRequestPublisher(
            bus,
            Options.Create(new SharedAuditingOptions { ApplicationName = "identity-service" }),
            new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(signingOptions)),
            new RecordingCurrentTenant());

        await publisher.PublishAsync(
            new AuditLogWriteRequest
            {
                Category = "http.request",
                RedactionApplied = true,
                RedactionVersion = 1
            });

        var published = Assert.IsType<AuditLogWriteRequestedHostEto>(bus.LastEventData);
        Assert.Equal("audit-key-1", published.SignatureKeyId);
        Assert.True(published.SignatureTimestampUtc.HasValue);
        Assert.False(string.IsNullOrWhiteSpace(published.Signature));
    }

    [Fact]
    public async Task PublishAsync_should_send_a_host_context_tenant_record_under_the_record_tenant()
    {
        // Regression: the token endpoint audits failed tenant logins without an ambient tenant. Outbound stamping
        // requires the ambient tenant to equal the tenant contract's TenantId, so the publisher enters the record's
        // tenant scope for the send and restores the host scope afterwards.
        var tenantId = Guid.NewGuid();
        var currentTenant = new RecordingCurrentTenant();
        var bus = new CapturingMessageSender(currentTenant);
        var publisher = new AuditLogWriteRequestPublisher(
            bus,
            Options.Create(new SharedAuditingOptions { ApplicationName = "auth" }),
            new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(new AuditLogSignatureOptions())),
            currentTenant);

        await publisher.PublishAsync(new AuditLogWriteRequest
        {
            Category = "auth.token",
            TenantId = tenantId,
            RedactionApplied = true,
            RedactionVersion = 1
        });

        Assert.IsType<AuditLogWriteRequestedTenantEto>(bus.LastEventData);
        Assert.Equal(tenantId, bus.TenantAtSend);
        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public async Task PublishAsync_should_not_replace_a_different_ambient_tenant()
    {
        var ambientTenant = Guid.NewGuid();
        var currentTenant = new RecordingCurrentTenant();
        var bus = new CapturingMessageSender(currentTenant);
        var publisher = new AuditLogWriteRequestPublisher(
            bus,
            Options.Create(new SharedAuditingOptions { ApplicationName = "auth" }),
            new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(new AuditLogSignatureOptions())),
            currentTenant);

        using (currentTenant.Change(ambientTenant))
        {
            await publisher.PublishAsync(new AuditLogWriteRequest
            {
                Category = "auth.token",
                TenantId = Guid.NewGuid(),
                RedactionApplied = true,
                RedactionVersion = 1
            });
        }

        // The ambient scope is left intact, so outbound stamping rejects the cross-tenant command.
        Assert.Equal(ambientTenant, bus.TenantAtSend);
    }

    [Fact]
    public async Task PublishAsync_should_fail_when_redaction_metadata_is_missing()
    {
        var bus = new CapturingMessageSender();
        var publisher = new AuditLogWriteRequestPublisher(
            bus,
            Microsoft.Extensions.Options.Options.Create(new SharedAuditingOptions
            {
                ApplicationName = "tenant-service"
            }),
            new AuditLogEventSigner(new TestOptionsMonitor<AuditLogSignatureOptions>(new AuditLogSignatureOptions())),
            new RecordingCurrentTenant());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
            new AuditLogWriteRequest
            {
                Category = "http.request",
                RedactionApplied = false,
                RedactionVersion = 0
            }));

        Assert.Contains("redacted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedAuditLogsModule_should_allow_missing_application_name()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["AuditLogging:IsEnabled"] = "true"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var context = new ServiceConfigurationContext(new ServiceCollection(), configuration);
        var module = new SharedAuditLogsModule();
        module.ConfigureServices(context);
    }

    [Fact]
    public void SharedAuditLogsModule_should_replace_fallback_store_with_eventbus_store_under_a_real_provider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditLogging:IsEnabled"] = "true",
                [TenantWrightMessagingProviderConfiguration.ProviderKey] = "MassTransit"
            })
            .Build();

        var services = new ServiceCollection();
        var context = new ServiceConfigurationContext(services, configuration);
        var module = new SharedAuditLogsModule();
        module.ConfigureServices(context);

        var storeDescriptors = services
            .Where(descriptor => descriptor.ServiceType == typeof(IAuditingStore))
            .ToList();

        Assert.Single(storeDescriptors);
        Assert.Equal(typeof(EventBusAuditingStore), storeDescriptors[0].ImplementationType);
    }

    [Fact]
    public async Task EventBusAuditingStore_should_redact_nested_parameters_before_publishing()
    {
        var publisher = new CapturingAuditLogWriteRequestPublisher();
        var store = new EventBusAuditingStore(
            publisher,
            Options.Create(new SharedAuditingOptions { HideErrors = false }),
            Options.Create(new AuditRedactionOptions
            {
                Mask = "[MASKED]",
                SensitiveKeys = ["password"]
            }),
            NullLogger<EventBusAuditingStore>.Instance);

        await store.SaveAsync(new AuditLogInfo
        {
            ApplicationName = "identity",
            Category = "method",
            ExecutionTime = DateTime.UtcNow,
            Actions =
            [
                new AuditLogActionInfo
                {
                    ServiceName = "IdentityService",
                    MethodName = "ChangePassword",
                    Parameters = "{\"payload\":{\"password\":\"secret\",\"displayName\":\"Jane\"}}"
                }
            ],
            Exceptions =
            {
                new AuditLogExceptionInfo
                {
                    Type = "System.InvalidOperationException",
                    Message = "boom",
                    StackTrace = "at secret frame"
                }
            }
        });

        Assert.NotNull(publisher.LastRequest);
        var request = publisher.LastRequest!;
        Assert.True(request.RedactionApplied);
        Assert.Equal(1, request.RedactionVersion);
        Assert.Single(request.Actions);
        var payload = Assert.Contains("payload", request.Actions.Single().Parameters);
        Assert.Contains("[MASKED]", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", payload, StringComparison.Ordinal);
        Assert.Null(request.Exceptions.Single().StackTrace);
    }

    [Fact]
    public async Task EventBusAuditingStore_should_drop_invalid_parameter_payload_instead_of_publishing_raw()
    {
        var publisher = new CapturingAuditLogWriteRequestPublisher();
        var store = new EventBusAuditingStore(
            publisher,
            Options.Create(new SharedAuditingOptions { HideErrors = false }),
            Options.Create(new AuditRedactionOptions()),
            NullLogger<EventBusAuditingStore>.Instance);

        var action = new AuditLogActionInfo
        {
            ServiceName = "IdentityService",
            MethodName = "Import",
            Parameters = "{invalid-json"
        };
        action.ExtraProperties["source"] = "test";

        await store.SaveAsync(new AuditLogInfo
        {
            ApplicationName = "identity",
            Category = "method",
            ExecutionTime = DateTime.UtcNow,
            Actions = [action]
        });

        Assert.NotNull(publisher.LastRequest);
        var parameters = publisher.LastRequest!.Actions.Single().Parameters;
        Assert.Equal("test", parameters["source"]);
        Assert.DoesNotContain("raw", parameters.Keys);
        Assert.DoesNotContain("{invalid-json", parameters.Values);
    }

    private sealed class CapturingMessageSender(ICurrentTenant? currentTenant = null) : ITenantWrightMessageSender
    {
        public Type? LastEventType { get; private set; }
        public object? LastEventData { get; private set; }
        public bool LastWasCommand { get; private set; }
        public Guid? TenantAtSend { get; private set; }

        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class
        {
            LastEventType = typeof(T);
            LastEventData = command;
            LastWasCommand = true;
            TenantAtSend = currentTenant?.Id;
            return Task.CompletedTask;
        }

        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            LastEventType = typeof(T);
            LastEventData = message;
            LastWasCommand = false;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAuditLogWriteRequestPublisher : IAuditLogWriteRequestPublisher
    {
        public AuditLogWriteRequest? LastRequest { get; private set; }

        public Task PublishAsync(
            AuditLogWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name { get; private set; }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            var previousName = Name;
            Id = id;
            Name = name;
            return new Restore(() =>
            {
                Id = previousId;
                Name = previousName;
            });
        }

        private sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

}
