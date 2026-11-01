using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Permissions.Application.PermissionManagement;
using Permissions.Tests.TestDoubles;
using Xunit;

namespace Permissions.Tests;

public sealed class IdentityPermissionTargetValidatorTests
{
    [Fact]
    public async Task GetRoleAsync_uses_internal_token_and_explicit_tenant_scope()
    {
        var roleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        AuthenticationHeaderValue? authorization = null;
        string? requestUri = null;
        var factory = new TestHttpClientFactory(
            tokenHandler: _ => JsonResponse("""{"access_token":"internal-token"}"""),
            validationHandler: request =>
            {
                authorization = request.Headers.Authorization;
                requestUri = request.RequestUri?.PathAndQuery;
                return JsonResponse("""{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","name":"PlatformSupport","isStatic":false,"lifecycleState":0,"lifecycleVersion":1}""");
            });
        var validator = CreateValidator(factory);

        var tenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var result = await validator.GetRoleAsync(roleId, tenantId);

        Assert.Equal("PlatformSupport", result.Name);
        Assert.False(result.IsStatic);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("internal-token", authorization?.Parameter);
        Assert.Equal($"/api/identity/internal/permission-targets/roles/{roleId:D}?tenantId={tenantId:D}", requestUri);
    }

    [Fact]
    public async Task GetUserAsync_propagates_current_account_eligibility_and_active_role_ids()
    {
        var userId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var roleId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var factory = new TestHttpClientFactory(
            tokenHandler: _ => JsonResponse("""{"access_token":"internal-token"}"""),
            validationHandler: _ => JsonResponse(
                $"{{\"id\":\"{userId:D}\",\"userName\":\"clinician\",\"displayName\":\"Clinician\",\"isActive\":true,\"isLockedOut\":true,\"roleIds\":[\"{roleId:D}\"]}}"));
        var validator = CreateValidator(factory);

        var result = await validator.GetUserAsync(userId, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));

        Assert.Equal(userId, result.UserId);
        Assert.True(result.IsActive);
        Assert.True(result.IsLockedOut);
        Assert.Equal([roleId], result.RoleIds);
    }

    [Fact]
    public async Task GetUserAsync_rejects_missing_identity_target()
    {
        var factory = new TestHttpClientFactory(
            tokenHandler: _ => JsonResponse("""{"access_token":"internal-token"}"""),
            validationHandler: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var validator = CreateValidator(factory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            validator.GetUserAsync(Guid.Parse("bb507481-d9b7-4cb6-a1d3-d6d2f216071f"), tenantId: null));
    }

    [Fact]
    public async Task GetRoleAsync_reuses_cached_internal_token_across_calls()
    {
        var roleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tokenRequests = 0;
        var factory = new TestHttpClientFactory(
            tokenHandler: _ =>
            {
                Interlocked.Increment(ref tokenRequests);
                return JsonResponse("""{"access_token":"internal-token","expires_in":3600}""");
            },
            validationHandler: _ => JsonResponse("""{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","name":"PlatformSupport","isStatic":false,"lifecycleState":0,"lifecycleVersion":1}"""));
        var validator = CreateValidator(factory);

        await validator.GetRoleAsync(roleId, tenantId: null);
        await validator.GetRoleAsync(roleId, tenantId: null);

        Assert.Equal(1, tokenRequests);
    }

    private static IdentityPermissionTargetValidator CreateValidator(TestHttpClientFactory factory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthServer:Authority"] = "http://auth.local"
            })
            .Build();
        var options = Options.Create(new IdentityPermissionTargetValidationOptions
        {
            BaseAddress = "http://identity.local/",
            Scope = "identity-internal",
            ClientId = "auth-internal",
            ClientSecret = "secret"
        });
        var tokenProvider = new PermissionIdentityInternalAccessTokenProvider(
            factory,
            configuration,
            options,
            new InMemoryDistributedCache<PermissionIdentityInternalTokenCacheEntry>(),
            new NoopDistributedLock(),
            NullLogger<PermissionIdentityInternalAccessTokenProvider>.Instance);

        return new IdentityPermissionTargetValidator(
            factory,
            tokenProvider,
            options,
            NullLogger<IdentityPermissionTargetValidator>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _tokenHandler;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _validationHandler;

        public TestHttpClientFactory(
            Func<HttpRequestMessage, HttpResponseMessage> tokenHandler,
            Func<HttpRequestMessage, HttpResponseMessage> validationHandler)
        {
            _tokenHandler = tokenHandler;
            _validationHandler = validationHandler;
        }

        public HttpClient CreateClient(string name)
        {
            if (name == nameof(PermissionIdentityInternalAccessTokenProvider))
            {
                return new HttpClient(new DelegateHandler(_tokenHandler));
            }

            return new HttpClient(new DelegateHandler(_validationHandler))
            {
                BaseAddress = new Uri("http://identity.local/")
            };
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
