using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class TenantWrightInboundMessagePipelineTests
{
    [Fact]
    public async Task Duplicate_delivery_should_be_acknowledged_without_invoking_the_handler_again()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var currentTenant = new MessagingTestCurrentTenant();
        var recorder = new TenantRecordingHandler(currentTenant);
        var inbox = new InMemoryInboxTransaction(Services(recorder));
        var pipeline = fixture.Pipeline(currentTenant, inbox);
        var message = NeutralTrustFixture.TenantMessage(tenantId);
        var stamped = fixture.Stamper(new MessagingTestCurrentTenant(tenantId)).Stamp(message, TenantWrightMessageRouteKind.Event);
        var delivery = NeutralTrustFixture.Delivery(stamped, message);

        await pipeline.ProcessAsync<PermissionGrantsChangedTenantEto>(delivery, default);
        await pipeline.ProcessAsync<PermissionGrantsChangedTenantEto>(delivery, default);

        Assert.Equal(1, recorder.InvocationCount);
        Assert.Equal(2, inbox.AttemptCount);
    }

    [Fact]
    public async Task Verified_tenant_should_be_ambient_inside_the_handler_and_reverted_afterwards()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var outerTenant = Guid.NewGuid();
        var currentTenant = new MessagingTestCurrentTenant();
        var recorder = new TenantRecordingHandler(currentTenant);
        var pipeline = fixture.Pipeline(currentTenant, new InMemoryInboxTransaction(Services(recorder)));
        var message = NeutralTrustFixture.TenantMessage(tenantId);
        var stamped = fixture.Stamper(new MessagingTestCurrentTenant(tenantId)).Stamp(message, TenantWrightMessageRouteKind.Event);

        using (currentTenant.Change(outerTenant))
        {
            await pipeline.ProcessAsync<PermissionGrantsChangedTenantEto>(NeutralTrustFixture.Delivery(stamped, message), default);
            Assert.Equal(outerTenant, currentTenant.Id);
        }

        Assert.Equal(tenantId, recorder.ObservedTenant);
        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public async Task Tenant_should_be_reverted_and_the_message_left_unrecorded_when_the_handler_fails()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var currentTenant = new MessagingTestCurrentTenant();
        var recorder = new TenantRecordingHandler(currentTenant) { FailNext = true };
        var inbox = new InMemoryInboxTransaction(Services(recorder));
        var pipeline = fixture.Pipeline(currentTenant, inbox);
        var message = NeutralTrustFixture.TenantMessage(tenantId);
        var stamped = fixture.Stamper(new MessagingTestCurrentTenant(tenantId)).Stamp(message, TenantWrightMessageRouteKind.Event);
        var delivery = NeutralTrustFixture.Delivery(stamped, message);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ProcessAsync<PermissionGrantsChangedTenantEto>(delivery, default));
        Assert.Null(currentTenant.Id);

        // A failed attempt does not consume the inbox entry: the retry runs the handler.
        await pipeline.ProcessAsync<PermissionGrantsChangedTenantEto>(delivery, default);
        Assert.Equal(2, recorder.InvocationCount);
    }

    [Fact]
    public async Task Rejected_delivery_should_never_reach_the_inbox_or_the_handler()
    {
        using var fixture = new NeutralTrustFixture();
        var currentTenant = new MessagingTestCurrentTenant();
        var recorder = new TenantRecordingHandler(currentTenant);
        var inbox = new InMemoryInboxTransaction(Services(recorder));
        var pipeline = fixture.Pipeline(currentTenant, inbox);
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            pipeline.ProcessAsync<TenantDeactivatedEto>(
                NeutralTrustFixture.Delivery(stamped, message, brokerActor: NeutralTrustFixture.UntrustedPrincipal),
                default));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
        Assert.Equal(0, inbox.AttemptCount);
        Assert.Equal(0, recorder.InvocationCount);
    }

    [Fact]
    public async Task Delivery_whose_runtime_type_differs_from_the_consumer_contract_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var currentTenant = new MessagingTestCurrentTenant();
        var inbox = new InMemoryInboxTransaction(Services(new TenantRecordingHandler(currentTenant)));
        var pipeline = fixture.Pipeline(currentTenant, inbox);
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            pipeline.ProcessAsync<PermissionGrantsChangedTenantEto>(NeutralTrustFixture.Delivery(stamped, message), default));

        Assert.Equal(MessagingSecurityException.UnsupportedContract, exception.Message);
        Assert.Equal(0, inbox.AttemptCount);
    }

    private static ServiceProvider Services(TenantRecordingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>>(handler);
        services.AddSingleton<ITenantWrightMessageHandler<TenantDeactivatedEto>>(handler);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class TenantRecordingHandler(MessagingTestCurrentTenant currentTenant)
        : ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>, ITenantWrightMessageHandler<TenantDeactivatedEto>
    {
        public int InvocationCount { get; private set; }
        public Guid? ObservedTenant { get; private set; }
        public bool FailNext { get; set; }

        public Task HandleAsync(PermissionGrantsChangedTenantEto message, CancellationToken cancellationToken) => Record();

        public Task HandleAsync(TenantDeactivatedEto message, CancellationToken cancellationToken) => Record();

        private Task Record()
        {
            InvocationCount++;
            ObservedTenant = currentTenant.Id;
            if (!FailNext)
                return Task.CompletedTask;
            FailNext = false;
            throw new InvalidOperationException("handler failed");
        }
    }
}
