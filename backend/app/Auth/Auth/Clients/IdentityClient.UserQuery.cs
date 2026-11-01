using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Auth.Caching;
using Auth.Clients.Caching;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;
using Identity.Application.Contracts.Users;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;

namespace Auth.Clients;

public partial class IdentityClient
{
    public async Task<UserInfoDto?> GetUserInfoAsync(
        string loginInput,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(loginInput))
        {
            return null;
        }

        var effectiveTenantId = mode == InteractiveLoginMode.Platform ? null : tenantId;
        var aliasKey = IdentityUserCacheKeys.Alias(effectiveTenantId, loginInput);
        var aliasEnvelope = await _aliasCache.GetAsync(aliasKey, token: cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (aliasEnvelope != null && !IsHardExpired(aliasEnvelope.HardExpireAtUtc, now))
        {
            var cachedById = await GetUserInfoByIdAsync(
                aliasEnvelope.Payload.UserId,
                effectiveTenantId,
                strictAuthorization: true,
                bypassCache: false,
                cancellationToken: cancellationToken);

            if (cachedById != null)
            {
                if (IsSoftExpired(aliasEnvelope.SoftExpireAtUtc, now))
                {
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await Task.Delay(BackgroundRefreshDelay).ConfigureAwait(false);
                                await RefreshAliasAsync(loginInput, effectiveTenantId, mode).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Identity alias background refresh failed.");
                            }
                        },
                        CancellationToken.None);
                }

                return cachedById;
            }
        }

        var fresh = await FetchUserInfoByLoginAsync(loginInput, effectiveTenantId, mode, cancellationToken);
        if (fresh == null)
        {
            return null;
        }

        await UpsertAliasAsync(aliasKey, fresh.Id, effectiveTenantId, cancellationToken);
        await UpsertProfileAsync(fresh, null, cancellationToken);
        await UpsertAuthorizationAsync(fresh, null, cancellationToken);
        return fresh;
    }
    public async Task<UserInfoDto?> GetUserInfoByIdAsync(
        Guid userId,
        Guid? tenantId = null,
        bool strictAuthorization = false,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var profileKey = IdentityUserCacheKeys.Profile(tenantId, userId);
        var authzKey = IdentityUserCacheKeys.Authz(tenantId, userId);

        IdentityProfileCacheEnvelope? profileEnvelope = null;
        IdentityAuthorizationCacheEnvelope? authzEnvelope = null;

        if (!bypassCache)
        {
            var profileTask = _profileCache.GetAsync(profileKey, token: cancellationToken);
            var authzTask = _authorizationCache.GetAsync(authzKey, token: cancellationToken);
            await Task.WhenAll(profileTask, authzTask);
            profileEnvelope = await profileTask;
            authzEnvelope = await authzTask;
        }

        profileEnvelope = await EnsureProfileAsync(
            profileEnvelope,
            userId,
            tenantId,
            bypassCache,
            cancellationToken);

        if (profileEnvelope == null)
        {
            return null;
        }

        authzEnvelope = await EnsureAuthorizationAsync(
            authzEnvelope,
            userId,
            tenantId,
            strictAuthorization,
            bypassCache,
            cancellationToken);

        if (authzEnvelope == null)
        {
            return null;
        }

        return ToUserInfo(profileEnvelope.Payload, authzEnvelope.Payload);
    }

}
