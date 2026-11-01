using Auth.Clients;
using Auth.Registration;
using Tenant.Application.Contracts.Tenants;
using Xunit;

namespace Auth.Tests;

public sealed class RegistrationProvisioningServiceTests
{
    [Fact]
    public async Task RegisterAsync_starts_one_public_tenant_operation_without_creating_a_user()
    {
        var client = new FakeTenantClient { Result = new PublicTenantRegistrationInitiateResult { TenantId = Guid.NewGuid(), OwnershipRequestId = Guid.NewGuid() } };
        var result = await new RegistrationProvisioningService(client, new FakeStatusTokenService()).RegisterAsync(new RegistrationProvisioningRequest
        {
            IdempotencyToken = "0123456789abcdef0123456789abcdef",
            Tenant = "acme",
            Email = "owner@example.com",
            UserName = "owner"
        });
        Assert.True(result.Succeeded);
        Assert.Equal(1, client.PublicRegistrationCalls);
        Assert.Equal("public-registration:0123456789abcdef0123456789abcdef", client.IdempotencyKey);
        Assert.Equal("signed-status", result.StatusToken);
    }

    [Fact]
    public async Task RegisterAsync_rejects_invalid_operation_id_without_calling_tenant()
    {
        var client = new FakeTenantClient();
        var result = await new RegistrationProvisioningService(client, new FakeStatusTokenService()).RegisterAsync(new RegistrationProvisioningRequest { IdempotencyToken = "bad" });
        Assert.False(result.Succeeded);
        Assert.Equal(0, client.PublicRegistrationCalls);
    }

    private sealed class FakeTenantClient : ITenantClient
    {
        public PublicTenantRegistrationInitiateResult? Result { get; set; }
        public int PublicRegistrationCalls { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public Task<TenantConfigurationDto?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfigurationDto?>(null);
        public Task<TenantConfigurationDto?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfigurationDto?>(null);
        public Task<PublicTenantRegistrationInitiateResult?> InitiatePublicRegistrationAsync(PublicTenantRegistrationInitiateInput input, string idempotencyKey, CancellationToken cancellationToken = default) { PublicRegistrationCalls++; IdempotencyKey = idempotencyKey; return Task.FromResult(Result); }
        public Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.FromResult<TenantProvisioningOperationDto?>(null);
    }

    private sealed class FakeStatusTokenService : IPublicRegistrationStatusTokenService
    {
        public string Protect(Guid operationId) => "signed-status";
        public bool TryUnprotect(string? token, out Guid operationId) { operationId = Guid.Empty; return false; }
    }
}
