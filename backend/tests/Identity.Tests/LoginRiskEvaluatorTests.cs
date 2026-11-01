using System.Security.Cryptography;
using System.Text;
using Identity.Application.Account.Risk;
using Identity.Application.Contracts.Account.Risk;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Identity.Tests;

public class LoginRiskEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_Should_Return_Medium_For_New_Device_And_New_Ip()
    {
        await using var dbContext = CreateDbContext();
        var evaluator = CreateEvaluator(dbContext, countryCode: null);

        var result = await evaluator.EvaluateAsync(new LoginRiskEvaluationContext
        {
            UserId = Guid.NewGuid(),
            TenantId = null,
            DeviceFingerprint = "device-a",
            ClientIpAddress = "10.10.10.1"
        });

        Assert.Equal(45, result.Score);
        Assert.Equal(LoginRiskLevel.Medium, result.Level);
        Assert.Equal(LoginRiskAction.ForceMfa, result.Action);
        Assert.Contains("new_device", result.Reasons);
        Assert.Contains("new_ip", result.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_Should_Return_Low_For_Known_Device_And_Known_Ip()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var deviceFingerprint = "known-device";
        var ipAddress = "10.10.10.2";
        var now = DateTime.UtcNow;

        dbContext.IdentityTrustedDevices.Add(new IdentityTrustedDevice
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserId = userId,
            DeviceHash = ComputeSha256Hex(deviceFingerprint),
            FirstSeenUtc = now.AddDays(-2),
            LastSeenUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddDays(10)
        });

        dbContext.IdentitySessions.Add(new IdentitySession
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserId = userId,
            SessionId = Guid.NewGuid().ToString("N"),
            IpAddress = ipAddress,
            LastActiveTime = now.AddMinutes(-5),
            CreationTime = now.AddDays(-1)
        });

        await dbContext.SaveChangesOnDbContextAsync(true);

        var evaluator = CreateEvaluator(dbContext, countryCode: null);
        var result = await evaluator.EvaluateAsync(new LoginRiskEvaluationContext
        {
            UserId = userId,
            TenantId = null,
            DeviceFingerprint = deviceFingerprint,
            ClientIpAddress = ipAddress
        });

        Assert.Equal(0, result.Score);
        Assert.Equal(LoginRiskLevel.Low, result.Level);
        Assert.Equal(LoginRiskAction.Allow, result.Action);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_Should_Ignore_Revoked_Session_History_For_Known_Ip()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        dbContext.IdentitySessions.Add(new IdentitySession
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserId = userId,
            SessionId = Guid.NewGuid().ToString("N"),
            IpAddress = "10.10.10.9",
            LastActiveTime = now.AddMinutes(-5),
            CreationTime = now.AddDays(-1),
            Status = IdentitySessionStatus.Revoked,
            RevokedAtUtc = now.AddMinutes(-1),
            RevocationSource = "BulkRevoke"
        });

        await dbContext.SaveChangesOnDbContextAsync(true);

        var evaluator = CreateEvaluator(dbContext, countryCode: null);
        var result = await evaluator.EvaluateAsync(new LoginRiskEvaluationContext
        {
            UserId = userId,
            TenantId = null,
            DeviceFingerprint = null,
            ClientIpAddress = "10.10.10.9"
        });

        Assert.Contains("new_ip", result.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_Should_Return_High_For_GeoShift_And_FailureBurst_With_NewIp()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var deviceFingerprint = "trusted-device";
        var now = DateTime.UtcNow;

        dbContext.IdentityTrustedDevices.Add(new IdentityTrustedDevice
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserId = userId,
            DeviceHash = ComputeSha256Hex(deviceFingerprint),
            FirstSeenUtc = now.AddDays(-10),
            LastSeenUtc = now.AddDays(-1),
            ExpiresAtUtc = now.AddDays(30)
        });

        dbContext.IdentitySecurityLogs.Add(new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserId = userId,
            Action = "LoginSuccess",
            CreationTime = now.AddHours(-1),
            ExtraProperties = "{\"countryCode\":\"US\"}"
        });

        dbContext.IdentitySecurityLogs.AddRange(
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = null,
                UserId = userId,
                Action = "LoginFailed",
                CreationTime = now.AddMinutes(-2)
            },
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = null,
                UserId = userId,
                Action = "LoginInvalidPassword",
                CreationTime = now.AddMinutes(-4)
            },
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = null,
                UserId = userId,
                Action = "TwoFactorInvalidCode",
                CreationTime = now.AddMinutes(-6)
            });

        await dbContext.SaveChangesOnDbContextAsync(true);

        var evaluator = CreateEvaluator(dbContext, countryCode: "IN", geoSignalTrusted: true);
        var result = await evaluator.EvaluateAsync(new LoginRiskEvaluationContext
        {
            UserId = userId,
            TenantId = null,
            DeviceFingerprint = deviceFingerprint,
            ClientIpAddress = "172.16.20.9"
        });

        Assert.Equal(LoginRiskLevel.High, result.Level);
        Assert.Equal(LoginRiskAction.Block, result.Action);
        Assert.Contains("new_ip", result.Reasons);
        Assert.Contains("geo_shift", result.Reasons);
        Assert.Contains("failure_burst", result.Reasons);
        Assert.True(result.Score >= 70);
    }

    [Fact]
    public async Task EvaluateAsync_Should_Apply_Active_Override_After_Scoring()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var overrideId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        dbContext.IdentityLoginRiskOverrides.Add(new IdentityLoginRiskOverride
        {
            Id = overrideId,
            TenantId = null,
            UserId = userId,
            ModeOverride = IdentityLoginRiskOverrideMode.Block,
            Reason = "Manual SOC block",
            CreationTime = now,
            IsActive = true,
            ExpiresAtUtc = now.AddDays(1)
        });

        await dbContext.SaveChangesOnDbContextAsync(true);

        var evaluator = CreateEvaluator(dbContext, countryCode: null);
        var result = await evaluator.EvaluateAsync(new LoginRiskEvaluationContext
        {
            UserId = userId,
            TenantId = null,
            DeviceFingerprint = "known-device",
            ClientIpAddress = "10.0.0.1"
        });

        Assert.Equal(overrideId, result.AppliedOverrideId);
        Assert.Equal(LoginRiskLevel.High, result.Level);
        Assert.Equal(LoginRiskAction.Block, result.Action);
        Assert.Contains("override:block", result.Reasons);
    }

    private static IdentityDbContextApp CreateDbContext()
    {
        return IdentityTestSupport.CreateDbContext();
    }

    private static LoginRiskEvaluator CreateEvaluator(
        IdentityDbContextApp dbContext,
        string? countryCode,
        bool geoSignalTrusted = false)
    {
        return new LoginRiskEvaluator(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new StubGeoResolver(countryCode),
            Options.Create(new LoginRiskOptions
            {
                GeoSignalTrusted = geoSignalTrusted,
                GeoShiftWeight = geoSignalTrusted ? 25 : 0
            }));
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class StubGeoResolver : ILoginGeoResolver
    {
        private readonly string? _countryCode;

        public StubGeoResolver(string? countryCode)
        {
            _countryCode = countryCode;
        }

        public string? ResolveCountryCode() => _countryCode;
    }
}
