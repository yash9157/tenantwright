using Auth.Clients;
using Shared.Modularity.DependencyInjection;
using Tenant.Application.Contracts.Tenants;

namespace Auth.Registration;

/// <summary>
/// Auth's public-edge orchestration only. Tenant owns creation of the
/// Provisioning tenant and Identity owns invitation/account verification.
/// </summary>
public interface IRegistrationProvisioningService
{
    Task<RegistrationProvisioningResult> RegisterAsync(RegistrationProvisioningRequest request, CancellationToken cancellationToken = default);
}

public sealed class RegistrationProvisioningService : IRegistrationProvisioningService, ITransientDependency
{
    private readonly ITenantClient _tenantClient;
    private readonly IPublicRegistrationStatusTokenService _statusTokenService;

    public RegistrationProvisioningService(
        ITenantClient tenantClient,
        IPublicRegistrationStatusTokenService statusTokenService)
    {
        _tenantClient = tenantClient;
        _statusTokenService = statusTokenService;
    }

    public async Task<RegistrationProvisioningResult> RegisterAsync(RegistrationProvisioningRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.IdempotencyToken, "N", out var operationId)) return RegistrationProvisioningResult.Failed();
        var result = await _tenantClient.InitiatePublicRegistrationAsync(new PublicTenantRegistrationInitiateInput
        {
            OperationId = operationId,
            Name = request.Tenant,
            OwnerEmail = request.Email,
            SuggestedUserName = request.UserName
        }, $"public-registration:{operationId:N}", cancellationToken);
        return result == null
            ? RegistrationProvisioningResult.Failed()
            : RegistrationProvisioningResult.Success(_statusTokenService.Protect(operationId));
    }
}

public sealed class RegistrationProvisioningRequest
{
    public string IdempotencyToken { get; init; } = string.Empty;
    public string Tenant { get; init; } = string.Empty;
    public string? UserName { get; init; }
    public string Email { get; init; } = string.Empty;
}

public sealed class RegistrationProvisioningResult
{
    public bool Succeeded { get; private init; }
    public string StatusToken { get; private init; } = string.Empty;
    public static RegistrationProvisioningResult Success(string statusToken) => new() { Succeeded = true, StatusToken = statusToken };
    public static RegistrationProvisioningResult Failed() => new();
}
