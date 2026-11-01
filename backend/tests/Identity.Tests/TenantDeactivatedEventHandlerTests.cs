using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Shared.EventContracts;
using Xunit;

namespace Identity.Tests;

public class TenantDeactivatedEventHandlerTests
{
    [Fact]
    public async Task HandleEventAsync_should_revoke_all_tenant_sessions_with_tenant_deactivated_source()
    {
        var service = new RecordingSessionRevocationService();
        var handler = new TenantDeactivatedEventHandler(service);
        var tenantId = Guid.NewGuid();

        await handler.HandleAsync(new TenantDeactivatedEto
        {
            TenantId = tenantId,
            TenantName = "north-org",
            OccurredAtUtc = DateTime.UtcNow
        });

        Assert.Equal(tenantId, service.LastTenantId);
        Assert.Equal("TenantDeactivated", service.LastAction);
        Assert.Equal("TenantDeactivated", service.LastRevocationSource);
    }

    private sealed class RecordingSessionRevocationService : IIdentitySessionRevocationService
    {
        public Guid LastTenantId { get; private set; }
        public string? LastAction { get; private set; }
        public string? LastRevocationSource { get; private set; }

        public Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> RevokeAllByTenantAsync(Guid tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            LastAction = action;
            LastRevocationSource = revocationSource;
            return Task.FromResult(0);
        }

        public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(IReadOnlyCollection<Guid> sessionIds, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BulkRevokeSessionsResultDto());
    }
}
