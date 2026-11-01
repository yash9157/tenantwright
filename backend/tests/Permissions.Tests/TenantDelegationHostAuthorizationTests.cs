using System.Security.Claims;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.TenantPermissionAccess;
using Permissions.Tests.TestDoubles;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using Xunit;

namespace Permissions.Tests;

public sealed class TenantDelegationHostAuthorizationTests
{
    [Fact]
    public async Task Host_read_rejects_an_ambient_tenant_before_authorization()
    {
        var authorization = new RecordingAuthorizationService();
        var service = CreateService(authorization, new TestCurrentTenant(Guid.NewGuid()), new TestCurrentUser("HostAdmin"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetHostCeilingAsync(Guid.NewGuid()));

        Assert.Empty(authorization.Policies);
    }

    [Fact]
    public async Task Host_read_rejects_a_non_host_principal_before_authorization()
    {
        var authorization = new RecordingAuthorizationService();
        var service = CreateService(authorization, new TestCurrentTenant(null), new TestCurrentUser("TenantAdmin"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetHostCeilingAsync(Guid.NewGuid()));

        Assert.Empty(authorization.Policies);
    }

    [Fact]
    public async Task Host_read_requires_the_manage_grants_permission()
    {
        var authorization = new RecordingAuthorizationService(throwAfterRecording: true);
        var service = CreateService(authorization, new TestCurrentTenant(null), new TestCurrentUser("HostAdmin"));

        await Assert.ThrowsAsync<ExpectedAuthorizationStopException>(() =>
            service.GetHostCeilingAsync(Guid.NewGuid()));

        Assert.Equal(["Permissions.Management.Grants"], authorization.Policies);
    }

    [Theory]
    [InlineData("", "HD-1042")]
    [InlineData("Approved coverage", "")]
    public async Task Host_mutation_requires_reason_and_ticket(string reason, string ticketId)
    {
        var service = CreateService(
            new RecordingAuthorizationService(),
            new TestCurrentTenant(null),
            new TestCurrentUser("HostAdmin"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateHostCeilingAsync(
            Guid.NewGuid(),
            "ceiling-change-1",
            new UpdateHostTenantDelegationCeilingInput
            {
                ExpectedProviderVersion = "00000000-0000-0000-0000-000000000000",
                Changes = [new HostTenantDelegationCeilingChangeDto { PermissionName = "Documents.Search", IsGranted = true }],
                Reason = reason,
                TicketId = ticketId
            }));
    }

    private static TenantDelegationAppService CreateService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser)
    {
        return new TenantDelegationAppService(
            authorization,
            currentTenant,
            currentUser,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
    }

    private sealed class RecordingAuthorizationService(bool throwAfterRecording = false) : IRequestAuthorizationService
    {
        public List<string> Policies { get; } = [];

        public Task EnsureAuthorizedAsync(string policyName, CancellationToken cancellationToken = default)
        {
            Policies.Add(policyName);
            if (throwAfterRecording)
                throw new ExpectedAuthorizationStopException();
            return Task.CompletedTask;
        }
    }

    private sealed class ExpectedAuthorizationStopException : Exception
    {
    }

    private sealed class TestCurrentUser(params string[] roles) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? Id { get; } = Guid.NewGuid();
        public string? UserName => "host.operator";
        public string? Name => null;
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => null;
        public string? SessionId => null;
        public string[] Roles { get; } = roles;
        public Claim? FindClaim(string claimType) => null;
        public Claim[] FindClaims(string claimType) => [];
        public Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => Roles.Contains(roleName, StringComparer.OrdinalIgnoreCase);
    }
}
