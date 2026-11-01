using Auth.Clients;
using Auth.Registration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Tenant.Application.Contracts.Tenants;
using Tenant.Domain.Shared.Enums;
using Xunit;

namespace Auth.Tests;

public sealed class PublicRegistrationStatusTests
{
    [Theory]
    [InlineData("1x00000000000000000000AA", "production-secret")]
    [InlineData("production-site-key", "1x0000000000000000000000000000000AA")]
    public void Turnstile_test_credentials_are_detected(string siteKey, string secret)
    {
        var options = new PublicTenantRegistrationOptions
        {
            TurnstileSiteKey = siteKey,
            TurnstileSecret = secret
        };

        Assert.True(options.UsesCloudflareTestCredentials());
    }

    [Fact]
    public void Non_test_Turnstile_credentials_are_not_flagged()
    {
        var options = new PublicTenantRegistrationOptions
        {
            TurnstileSiteKey = "production-site-key",
            TurnstileSecret = "production-secret"
        };

        Assert.False(options.UsesCloudflareTestCredentials());
    }

    [Fact]
    public void Status_token_round_trips_and_rejects_tampering()
    {
        var service = new PublicRegistrationStatusTokenService(
            new EphemeralDataProtectionProvider(),
            Options.Create(new PublicTenantRegistrationOptions { StatusTokenLifetimeHours = 24 }));
        var operationId = Guid.NewGuid();

        var token = service.Protect(operationId);

        Assert.DoesNotContain(operationId.ToString("N"), token, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.TryUnprotect(token, out var resolved));
        Assert.Equal(operationId, resolved);
        Assert.False(service.TryUnprotect(token + "x", out _));
    }

    [Theory]
    [InlineData(TenantOwnershipRequestStatus.PendingInvitation, false, false, "starting")]
    [InlineData(TenantOwnershipRequestStatus.AwaitingEmailVerification, false, false, "verification")]
    [InlineData(TenantOwnershipRequestStatus.Completed, false, false, "configuring-access")]
    [InlineData(TenantOwnershipRequestStatus.Completed, true, true, "ready")]
    [InlineData(TenantOwnershipRequestStatus.Expired, false, true, "stopped")]
    public async Task Status_service_maps_only_coarse_public_stages(
        TenantOwnershipRequestStatus requestStatus,
        bool isReady,
        bool isTerminal,
        string expectedStage)
    {
        var client = new FakeTenantClient
        {
            Status = new TenantProvisioningOperationDto
            {
                Status = requestStatus,
                TenantLifecycleStatus = isReady ? TenantLifecycleStatus.Active : TenantLifecycleStatus.Provisioning,
                IsReady = isReady,
                IsTerminal = isTerminal
            }
        };
        var service = new PublicRegistrationStatusService(new FakeTokenService(), client);

        var result = await service.GetAsync("valid");

        Assert.Equal(expectedStage, result.Stage);
        Assert.DoesNotContain(client.OperationId.ToString(), result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_and_unknown_operations_have_the_same_unavailable_result()
    {
        var invalid = await new PublicRegistrationStatusService(
            new FakeTokenService { IsValid = false },
            new FakeTenantClient()).GetAsync("bad");
        var unknown = await new PublicRegistrationStatusService(
            new FakeTokenService(),
            new FakeTenantClient()).GetAsync("valid");

        Assert.Equal("unavailable", invalid.Stage);
        Assert.Equal(invalid, unknown);
    }

    private sealed class FakeTokenService : IPublicRegistrationStatusTokenService
    {
        public bool IsValid { get; init; } = true;
        public Guid OperationId { get; } = Guid.NewGuid();
        public string Protect(Guid operationId) => "valid";
        public bool TryUnprotect(string? token, out Guid operationId)
        {
            operationId = IsValid ? OperationId : Guid.Empty;
            return IsValid;
        }
    }

    private sealed class FakeTenantClient : ITenantClient
    {
        public Guid OperationId { get; private set; }
        public TenantProvisioningOperationDto? Status { get; init; }
        public Task<TenantConfigurationDto?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfigurationDto?>(null);
        public Task<TenantConfigurationDto?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfigurationDto?>(null);
        public Task<PublicTenantRegistrationInitiateResult?> InitiatePublicRegistrationAsync(PublicTenantRegistrationInitiateInput input, string idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult<PublicTenantRegistrationInitiateResult?>(null);
        public Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            return Task.FromResult(Status);
        }
    }
}
