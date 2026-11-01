using Auth.Clients;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using System.Security.Cryptography;
using Tenant.Domain.Shared.Enums;

namespace Auth.Registration;

public interface IPublicRegistrationStatusTokenService
{
    string Protect(Guid operationId);
    bool TryUnprotect(string? token, out Guid operationId);
}

public sealed class PublicRegistrationStatusTokenService :
    IPublicRegistrationStatusTokenService,
    ISingletonDependency
{
    private const string Purpose = "TenantWright.Auth.PublicTenantRegistration.Status.v1";
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public PublicRegistrationStatusTokenService(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<PublicTenantRegistrationOptions> options)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _lifetime = TimeSpan.FromHours(Math.Clamp(options.Value.StatusTokenLifetimeHours, 1, 720));
    }

    public string Protect(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation id is required.", nameof(operationId));
        return _protector.Protect($"v1:{operationId:N}", _lifetime);
    }

    public bool TryUnprotect(string? token, out Guid operationId)
    {
        operationId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;

        try
        {
            var value = _protector.Unprotect(token, out _);
            return value.StartsWith("v1:", StringComparison.Ordinal) &&
                   Guid.TryParseExact(value[3..], "N", out operationId) &&
                   operationId != Guid.Empty;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            operationId = Guid.Empty;
            return false;
        }
    }
}

public interface IPublicRegistrationStatusService
{
    Task<PublicRegistrationStatusView> GetAsync(string? token, CancellationToken cancellationToken = default);
}

public sealed class PublicRegistrationStatusService :
    IPublicRegistrationStatusService,
    ITransientDependency
{
    private readonly IPublicRegistrationStatusTokenService _tokenService;
    private readonly ITenantClient _tenantClient;

    public PublicRegistrationStatusService(
        IPublicRegistrationStatusTokenService tokenService,
        ITenantClient tenantClient)
    {
        _tokenService = tokenService;
        _tenantClient = tenantClient;
    }

    public async Task<PublicRegistrationStatusView> GetAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (!_tokenService.TryUnprotect(token, out var operationId))
        {
            return PublicRegistrationStatusView.Unavailable();
        }

        var status = await _tenantClient.GetPublicRegistrationAsync(operationId, cancellationToken);
        if (status == null) return PublicRegistrationStatusView.Unavailable();
        if (status.IsReady) return new("ready", "Organization ready", "Your organization setup is complete. You can sign in now.", true);

        if (status.IsTerminal ||
            status.Status is TenantOwnershipRequestStatus.Canceled or TenantOwnershipRequestStatus.Expired or TenantOwnershipRequestStatus.Failed ||
            status.TenantLifecycleStatus is TenantLifecycleStatus.ProvisioningFailed or TenantLifecycleStatus.Decommissioned)
        {
            return new("stopped", "Setup could not continue", "This setup is no longer active. Start a new request or contact support.", false);
        }

        return status.Status switch
        {
            TenantOwnershipRequestStatus.PendingInvitation when status.InvitationCreatedAtUtc.HasValue =>
                new("invitation-sent", "Invitation sent", "Check your email and use the invitation link to continue setup.", false),
            TenantOwnershipRequestStatus.AwaitingEmailVerification =>
                new("verification", "Verification required", "Finish email verification to continue organization setup.", false),
            TenantOwnershipRequestStatus.Completed =>
                new("configuring-access", "Configuring access", "Your account is verified. We are finishing secure access setup.", false),
            _ => new("starting", "Starting setup", "We are preparing your secure organization invitation.", false)
        };
    }
}

public sealed record PublicRegistrationStatusView(
    string Stage,
    string Title,
    string Message,
    bool IsReady)
{
    public static PublicRegistrationStatusView Unavailable() =>
        new("unavailable", "Status unavailable", "This status link is invalid, expired, or temporarily unavailable.", false);
}
