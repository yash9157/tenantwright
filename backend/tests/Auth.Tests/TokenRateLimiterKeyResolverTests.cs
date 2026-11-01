using System.Net;
using Auth.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Auth.Tests;

public sealed class TokenRateLimiterKeyResolverTests
{
    [Fact]
    public void Resolve_UsesBasicAuthClientId_BeforeFormClientId()
    {
        var context = CreateContext("10.0.0.8");
        context.Request.Headers.Authorization = "Basic Y2xpZW50LWhkOnNlY3JldA=="; // client-hd:secret
        SetForm(context, new Dictionary<string, StringValues>
        {
            ["client_id"] = "client-form",
            ["grant_type"] = "client_credentials"
        });

        var result = TokenRateLimiterKeyResolver.Resolve(
            context,
            new TokenRateLimitingOptions { PartitionMode = TokenRateLimitPartitionMode.ClientIp });

        Assert.Equal("client-hd", result.ClientId);
        Assert.Equal("client_credentials", result.GrantType);
        Assert.Equal("client-hd|10.0.0.8", result.PartitionKey);
    }

    [Fact]
    public void Resolve_FallsBackToIp_WhenClientIdMissing()
    {
        var context = CreateContext("192.168.1.9");
        SetForm(context, new Dictionary<string, StringValues>
        {
            ["grant_type"] = "refresh_token"
        });

        var result = TokenRateLimiterKeyResolver.ResolveForRejection(
            context,
            new TokenRateLimitingOptions { PartitionMode = TokenRateLimitPartitionMode.ClientIp });

        Assert.Null(result.ClientId);
        Assert.Equal("refresh_token", result.GrantType);
        Assert.Equal("192.168.1.9", result.PartitionKey);
    }

    [Fact]
    public void Resolve_IpOnlyMode_UsesIpPartition()
    {
        var context = CreateContext("172.16.0.5");
        context.Request.Headers.Authorization = "Basic Y2xpZW50LWhkOnNlY3JldA=="; // client-hd:secret

        var result = TokenRateLimiterKeyResolver.ResolveForPartition(
            context,
            new TokenRateLimitingOptions { PartitionMode = TokenRateLimitPartitionMode.IpOnly });

        Assert.Null(result.ClientId);
        Assert.Equal("172.16.0.5", result.PartitionKey);
    }

    [Fact]
    public void HashPartitionKey_IsStable()
    {
        var hash1 = TokenRateLimiterKeyResolver.HashPartitionKey("abc|127.0.0.1");
        var hash2 = TokenRateLimiterKeyResolver.HashPartitionKey("abc|127.0.0.1");

        Assert.Equal(hash1, hash2);
        Assert.NotEqual("none", hash1);
    }

    [Fact]
    public void ResolveForPartition_DoesNotResolveGrantType()
    {
        var context = CreateContext("10.20.30.40");
        SetForm(context, new Dictionary<string, StringValues>
        {
            ["client_id"] = "client-form",
            ["grant_type"] = "refresh_token"
        });

        var result = TokenRateLimiterKeyResolver.ResolveForPartition(
            context,
            new TokenRateLimitingOptions { PartitionMode = TokenRateLimitPartitionMode.ClientIp });

        Assert.Equal("client-form", result.ClientId);
        Assert.Null(result.GrantType);
        Assert.Equal("client-form|10.20.30.40", result.PartitionKey);
    }

    private static DefaultHttpContext CreateContext(string ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        return context;
    }

    private static void SetForm(DefaultHttpContext context, Dictionary<string, StringValues> data)
    {
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(data)));
    }
}
