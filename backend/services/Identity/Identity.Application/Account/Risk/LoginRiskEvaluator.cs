using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Application.Contracts.Account.Risk;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Account.Risk;

public sealed class LoginRiskEvaluator : ILoginRiskEvaluator, ITransientDependency
{
    private static readonly string[] FailureActions =
    [
        "LoginFailed",
        "LoginInvalidPassword",
        "LoginLockedOut",
        "TwoFactorInvalidCode",
        "TwoFactorLockedOut"
    ];

    private static readonly string[] SuccessActions =
    [
        "LoginSuccess",
        "TwoFactorSuccess",
        "TwoFactorRecoverySuccess"
    ];

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ILoginGeoResolver _geoResolver;
    private readonly IOptions<LoginRiskOptions> _options;

    public LoginRiskEvaluator(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ILoginGeoResolver geoResolver, IOptions<LoginRiskOptions> options)
    {
        _dbContextProvider = dbContextProvider;
        _geoResolver = geoResolver;
        _options = options;
    }

    public async Task<LoginRiskEvaluationResult> EvaluateAsync(
        LoginRiskEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var options = _options.Value;
        if (!options.Enabled || context.UserId == Guid.Empty)
        {
            return new LoginRiskEvaluationResult
            {
                Level = LoginRiskLevel.Low,
                Action = LoginRiskAction.Allow,
                Score = 0,
                Reasons = ["risk_disabled_or_invalid_context"]
            };
        }

        var now = DateTime.UtcNow;
        var score = 0;
        var reasons = new List<string>();

        var normalizedFingerprint = NormalizeDeviceFingerprint(context.DeviceFingerprint);
        var deviceHash = ComputeSha256Hex(normalizedFingerprint);
        if (!string.IsNullOrWhiteSpace(deviceHash))
        {
            var hasTrustedDevice = await ApplyTenantScope(unitOfWorkDbContext.IdentityTrustedDevices.AsQueryable(), context.TenantId)
                .AnyAsync(
                    x => x.UserId == context.UserId &&
                         x.DeviceHash == deviceHash &&
                         x.ExpiresAtUtc > now,
                    cancellationToken);

            if (!hasTrustedDevice)
            {
                score += options.NewDeviceWeight;
                reasons.Add("new_device");
            }
        }

        if (!string.IsNullOrWhiteSpace(context.ClientIpAddress))
        {
            var historyBoundary = now.Subtract(options.HistoryWindow);
            var hasKnownIp = await ApplyTenantScope(unitOfWorkDbContext.IdentitySessions.AsQueryable(), context.TenantId)
                .AnyAsync(
                    x => x.UserId == context.UserId &&
                         x.Status == IdentitySessionStatus.Active &&
                         x.LastActiveTime >= historyBoundary &&
                         x.IpAddress == context.ClientIpAddress,
                    cancellationToken);

            if (!hasKnownIp)
            {
                score += options.NewIpWeight;
                reasons.Add("new_ip");
            }
        }

        var resolvedCountryCode = options.GeoSignalTrusted && options.GeoShiftWeight > 0
            ? _geoResolver.ResolveCountryCode()
            : null;
        if (!string.IsNullOrWhiteSpace(resolvedCountryCode))
        {
            var priorSuccessLogs = await ApplyTenantScope(unitOfWorkDbContext.IdentitySecurityLogs.AsQueryable(), context.TenantId)
                .Where(x => x.UserId == context.UserId && x.Action != null && SuccessActions.Contains(x.Action))
                .OrderByDescending(x => x.CreationTime)
                .Select(x => new CountryCodeSource
                {
                    CountryCode = x.CountryCode,
                    ExtraProperties = x.ExtraProperties
                })
                .Take(25)
                .ToListAsync(cancellationToken);

            var previousCountryCode = priorSuccessLogs
                .Select(ParseCountryCode)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (!string.IsNullOrWhiteSpace(previousCountryCode) &&
                !string.Equals(previousCountryCode, resolvedCountryCode, StringComparison.OrdinalIgnoreCase))
            {
                score += options.GeoShiftWeight;
                reasons.Add("geo_shift");
            }
        }

        var failureWindowStart = now.Subtract(options.FailureBurstWindow);
        var failureCount = await ApplyTenantScope(unitOfWorkDbContext.IdentitySecurityLogs.AsQueryable(), context.TenantId)
            .Where(x =>
                x.UserId == context.UserId &&
                x.CreationTime >= failureWindowStart &&
                x.Action != null &&
                FailureActions.Contains(x.Action))
            .CountAsync(cancellationToken);

        if (failureCount >= Math.Max(1, options.FailureBurstThreshold))
        {
            score += options.FailureBurstWeight;
            reasons.Add("failure_burst");
        }

        var result = new LoginRiskEvaluationResult
        {
            Score = score,
            CountryCode = resolvedCountryCode,
            Reasons = reasons
        };

        result.Level = score >= options.HighThreshold
            ? LoginRiskLevel.High
            : score >= options.LowThreshold
                ? LoginRiskLevel.Medium
                : LoginRiskLevel.Low;
        result.Action = result.Level switch
        {
            LoginRiskLevel.High => LoginRiskAction.Block,
            LoginRiskLevel.Medium => LoginRiskAction.ForceMfa,
            _ => LoginRiskAction.Allow
        };

        var activeOverride = await ApplyTenantScope(unitOfWorkDbContext.IdentityLoginRiskOverrides.AsQueryable(), context.TenantId)
            .Where(x =>
                x.UserId == context.UserId &&
                x.IsActive &&
                (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc.Value > now))
            .OrderByDescending(x => x.CreationTime)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeOverride != null)
        {
            result.AppliedOverrideId = activeOverride.Id;
            result.Action = activeOverride.ModeOverride switch
            {
                IdentityLoginRiskOverrideMode.Allow => LoginRiskAction.Allow,
                IdentityLoginRiskOverrideMode.ForceMfa => LoginRiskAction.ForceMfa,
                _ => LoginRiskAction.Block
            };

            result.Level = result.Action switch
            {
                LoginRiskAction.Block => LoginRiskLevel.High,
                LoginRiskAction.ForceMfa => LoginRiskLevel.Medium,
                _ => LoginRiskLevel.Low
            };
            result.Reasons.Add($"override:{activeOverride.ModeOverride.ToString().ToLowerInvariant()}");
        }

        return result;
    }

    private static string? ParseCountryCode(CountryCodeSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.CountryCode))
        {
            return source.CountryCode.ToUpperInvariant();
        }

        var extraPropertiesJson = source.ExtraProperties;
        if (string.IsNullOrWhiteSpace(extraPropertiesJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(extraPropertiesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("countryCode", out var countryProperty))
            {
                var countryCode = countryProperty.GetString();
                return string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.ToUpperInvariant();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? NormalizeDeviceFingerprint(string? deviceFingerprint)
    {
        if (string.IsNullOrWhiteSpace(deviceFingerprint))
        {
            return null;
        }

        var normalized = deviceFingerprint.Trim();
        return normalized.Length > 1024 ? normalized[..1024] : normalized;
    }

    private static string? ComputeSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IQueryable<T> ApplyTenantScope<T>(IQueryable<T> query, Guid? tenantId)
        where T : class, Shared.Domain.IMultiTenant
    {
        if (tenantId.HasValue)
        {
            return query.Where(x => x.TenantId == tenantId.Value);
        }

        return query.Where(x => x.TenantId == null);
    }

    private sealed class CountryCodeSource
    {
        public string? CountryCode { get; init; }
        public string? ExtraProperties { get; init; }
    }
}
