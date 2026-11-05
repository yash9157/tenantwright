using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.EntityFrameworkCore.Stores;
using Identity.Domain.ApplicationUsers;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Shared.AuditLogs.Auditing;
using Shared.BackgroundTasks.Jobs;
using Shared.Caching.Abstractions;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Options;
using Shared.Uow.Abstractions;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.Uow.Events;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace Identity.Tests;

internal static class IdentityTestSupport
{
    private static readonly ConditionalWeakTable<IdentityDbContextApp, IDataFilter> ContextDataFilters = new();

    public static ITenantWrightMessageSender CreateNoopMessageSender() => new NoopMessageSender();

    public sealed class CapturingMessageSender : ITenantWrightMessageSender
    {
        private readonly ICurrentTenant? _currentTenant;

        public CapturingMessageSender(ICurrentTenant? currentTenant = null)
        {
            _currentTenant = currentTenant;
        }

        public List<(object EventData, bool OnUnitOfWorkComplete, bool UseOutbox)> Published { get; } = [];
        public List<Guid?> CurrentTenantIds { get; } = [];
        public IReadOnlyList<CapturedNotificationRequest> Requests => Published
            .Select(record => record.EventData switch
            {
                NotificationSendRequestedTenantEto tenant => CapturedNotificationRequest.From(tenant),
                NotificationSendRequestedHostEto host => CapturedNotificationRequest.From(host),
                _ => null
            })
            .OfType<CapturedNotificationRequest>()
            .ToList();

        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class =>
            RecordAsync(command, onUnitOfWorkComplete: true, useOutbox: true);
        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class =>
            RecordAsync(message, onUnitOfWorkComplete: true, useOutbox: true);

        private Task RecordAsync(object eventData, bool onUnitOfWorkComplete, bool useOutbox)
        {
            Published.Add((eventData, onUnitOfWorkComplete, useOutbox));
            CurrentTenantIds.Add(_currentTenant?.Id);
            return Task.CompletedTask;
        }
    }

    public sealed record CapturedNotificationRequest(
        Guid? TenantId,
        Guid RequestId,
        string TemplateName,
        IReadOnlyDictionary<string, string?> TemplateModel,
        IReadOnlyList<NotificationRecipientEto> Recipients)
    {
        public static CapturedNotificationRequest From(NotificationSendRequestedTenantEto input)
            => new(input.TenantId, input.RequestId, input.DeliveryPlan.Steps.Single().TemplateName,
                input.DeliveryPlan.Steps.Single().TemplateModel, Destinations(input.Recipients));

        public static CapturedNotificationRequest From(NotificationSendRequestedHostEto input)
            => new(null, input.RequestId, input.DeliveryPlan.Steps.Single().TemplateName,
                input.DeliveryPlan.Steps.Single().TemplateModel, Destinations(input.Recipients));

        private static NotificationRecipientEto[] Destinations(IEnumerable<NotificationRecipientPlan> recipients) =>
            recipients.SelectMany(recipient => recipient.Destinations).Select(destination => new NotificationRecipientEto
            {
                RecipientType = destination.RecipientType,
                RecipientValue = destination.RecipientValue
            }).ToArray();
    }

    public static IdentityDbContextApp CreateDbContext(ICurrentTenant? currentTenant = null)
    {
        var contextTenant = currentTenant ?? new TestCurrentTenant(null);
        if (contextTenant is TestCurrentTenant testCurrentTenant)
        {
            TestCurrentTenant.SetCurrent(testCurrentTenant);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoopAuditingManager>();
        services.AddSingleton<Shared.Uow.Abstractions.IUnitOfWorkManager, NoopUnitOfWorkManager>();
        services.AddSingleton<ICurrentTenant>(contextTenant is TestCurrentTenant
            ? new ForwardingCurrentTenant()
            : contextTenant);
        var dataFilter = new EnabledDataFilter();
        services.AddSingleton<IDataFilter>(dataFilter);
        var provider = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<IdentityDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var dbContext = new IdentityDbContextApp(options)
        {
            ServiceProvider = provider
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork()));
        ContextDataFilters.Add(dbContext, dataFilter);
        return dbContext;
    }

    public static IdentityDbContextApp CreateSqliteDbContext(
        SqliteConnection connection,
        ICurrentTenant currentTenant)
    {
        if (currentTenant is TestCurrentTenant testCurrentTenant)
        {
            TestCurrentTenant.SetCurrent(testCurrentTenant);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoopAuditingManager>();
        services.AddSingleton<IUnitOfWorkManager, NoopUnitOfWorkManager>();
        services.AddSingleton<ICurrentTenant>(currentTenant is TestCurrentTenant
            ? new ForwardingCurrentTenant()
            : currentTenant);
        var dataFilter = new EnabledDataFilter();
        services.AddSingleton<IDataFilter>(dataFilter);

        var options = new DbContextOptionsBuilder<IdentityDbContextApp>()
            .UseSqlite(connection)
            .Options;
        var dbContext = new IdentityDbContextApp(options)
        {
            ServiceProvider = services.BuildServiceProvider()
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork()));
        ContextDataFilters.Add(dbContext, dataFilter);
        return dbContext;
    }

    public static IDbContextProvider<IdentityDbContextApp> AsDbContextProvider(IdentityDbContextApp dbContext)
        => new StaticDbContextProvider<IdentityDbContextApp>(dbContext);

    public static IDataFilter GetDataFilter(IdentityDbContextApp dbContext)
        => ContextDataFilters.TryGetValue(dbContext, out var dataFilter)
            ? dataFilter
            : throw new InvalidOperationException("The Identity test DbContext was not created by IdentityTestSupport.");

    public sealed class ImmediateUnitOfWorkExecutor : IUnitOfWorkExecutor
    {
        public Task ExecuteTransactionalAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);

        public Task<TResult> ExecuteTransactionalAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);

        public Task ExecuteNonTransactionalAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);

        public Task<TResult> ExecuteNonTransactionalAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);
    }

    public static ApplicationUserManager CreateUserManager(
        IdentityDbContextApp dbContext,
        ICurrentTenant currentTenant,
        IdentityOptions? identityOptions = null,
        RoleMembershipPolicyOptions? roleMembershipPolicy = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        services.AddSingleton(currentTenant);
        services.AddSingleton<IUserStore<ApplicationUser>>(sp => new EfApplicationUserStore(dbContext, currentTenant));
        services.AddSingleton<IRoleStore<ApplicationRole>>(sp => new EfApplicationRoleStore(dbContext, currentTenant));
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.AddSingleton<DataProtectorTokenProvider<ApplicationUser>>();
        services.AddSingleton<EmailTokenProvider<ApplicationUser>>();
        services.AddSingleton<PhoneNumberTokenProvider<ApplicationUser>>();
        services.AddSingleton<AuthenticatorTokenProvider<ApplicationUser>>();
        services.AddSingleton(Options.Create(new DataProtectionTokenProviderOptions()));
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var options = EnsureTokenProviders(identityOptions);

        return new ApplicationUserManager(
            provider.GetRequiredService<IUserStore<ApplicationUser>>(),
            Options.Create(options),
            new PasswordHasher<ApplicationUser>(),
            [new UserValidator<ApplicationUser>()],
            [new PasswordValidator<ApplicationUser>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            provider,
            NullLogger<ApplicationUserManager>.Instance,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            Options.Create(roleMembershipPolicy ?? new RoleMembershipPolicyOptions()));
    }

    private static IdentityOptions EnsureTokenProviders(IdentityOptions? identityOptions)
    {
        var options = identityOptions ?? new IdentityOptions();
        options.Tokens.ProviderMap[TokenOptions.DefaultProvider] =
            new TokenProviderDescriptor(typeof(DataProtectorTokenProvider<ApplicationUser>));
        options.Tokens.ProviderMap[TokenOptions.DefaultEmailProvider] =
            new TokenProviderDescriptor(typeof(EmailTokenProvider<ApplicationUser>));
        options.Tokens.ProviderMap[TokenOptions.DefaultPhoneProvider] =
            new TokenProviderDescriptor(typeof(PhoneNumberTokenProvider<ApplicationUser>));
        options.Tokens.ProviderMap[TokenOptions.DefaultAuthenticatorProvider] =
            new TokenProviderDescriptor(typeof(AuthenticatorTokenProvider<ApplicationUser>));
        options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultProvider;
        options.Tokens.PasswordResetTokenProvider = TokenOptions.DefaultProvider;
        options.Tokens.ChangeEmailTokenProvider = TokenOptions.DefaultProvider;
        options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;

        return options;
    }

    public static ApplicationRoleManager CreateRoleManager(IdentityDbContextApp dbContext, ICurrentTenant currentTenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        services.AddSingleton(currentTenant);
        services.AddSingleton<IRoleStore<ApplicationRole>>(sp => new EfApplicationRoleStore(dbContext, currentTenant));
        var provider = services.BuildServiceProvider();

        return new ApplicationRoleManager(
            provider.GetRequiredService<IRoleStore<ApplicationRole>>(),
            [new RoleValidator<ApplicationRole>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<ApplicationRoleManager>.Instance);
    }

    internal sealed class TestCurrentTenant : ICurrentTenant
    {
        private static readonly AsyncLocal<TestCurrentTenant?> CurrentTenant = new();

        public TestCurrentTenant(Guid? id, string? name = null)
        {
            Id = id;
            Name = name;
            CurrentTenant.Value = this;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name { get; private set; }

        internal static TestCurrentTenant? Current => CurrentTenant.Value;

        internal static void SetCurrent(TestCurrentTenant currentTenant)
        {
            CurrentTenant.Value = currentTenant;
        }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousCurrentTenant = CurrentTenant.Value;
            var previousId = Id;
            var previousName = Name;
            CurrentTenant.Value = this;
            Id = id;
            Name = name;
            return new DisposeAction(() =>
            {
                Id = previousId;
                Name = previousName;
                CurrentTenant.Value = previousCurrentTenant;
            });
        }
    }

    private sealed class ForwardingCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id => TestCurrentTenant.Current?.Id;
        public string? Name => TestCurrentTenant.Current?.Name;

        public IDisposable Change(Guid? id, string? name = null)
            => TestCurrentTenant.Current?.Change(id, name)
               ?? throw new InvalidOperationException("No test tenant context is active.");
    }

    internal sealed class TestCurrentUser : ICurrentUser
    {
        public TestCurrentUser(
            Guid? id,
            Guid? tenantId,
            string? userName,
            params string[] roles)
        {
            Id = id;
            TenantId = tenantId;
            UserName = userName;
            Name = userName;
            Roles = roles ?? [];
            IsAuthenticated = id.HasValue || Roles.Length > 0;
        }

        public bool IsAuthenticated { get; }
        public Guid? Id { get; }
        public string? UserName { get; }
        public string? Name { get; }
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId { get; }
        public string? SessionId => null;
        public string[] Roles { get; }

        public Claim? FindClaim(string claimType) => null;
        public Claim[] FindClaims(string claimType) => [];
        public Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => Roles.Any(x => string.Equals(x, roleName, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class EmptyTenantStore : ITenantStore
    {
        public IReadOnlyList<TenantConfiguration> GetList() => [];

        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TenantConfiguration>>([]);

        public TenantConfiguration? Find(Guid id) => null;

        public TenantConfiguration? Find(string normalizedName) => null;

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantConfiguration?>(null);

        public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantConfiguration?>(null);
    }

    internal sealed class TestTenantStore : ITenantStore
    {
        private readonly IReadOnlyList<TenantConfiguration> _tenants;

        public TestTenantStore(params TenantConfiguration[] tenants)
        {
            _tenants = tenants;
        }

        public IReadOnlyList<TenantConfiguration> GetList() => _tenants;

        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_tenants);

        public TenantConfiguration? Find(Guid id) => _tenants.SingleOrDefault(tenant => tenant.Id == id);

        public TenantConfiguration? Find(string normalizedName) => _tenants.SingleOrDefault(tenant =>
            string.Equals(tenant.NormalizedName, normalizedName, StringComparison.OrdinalIgnoreCase));

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Find(id));

        public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default)
            => Task.FromResult(Find(normalizedName));
    }

    internal sealed class InMemoryHybridCache<TCacheItem> : IHybridCache<TCacheItem, string>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _items = new(StringComparer.Ordinal);

        public Task<TCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<TCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            return GetOrCreateCoreAsync(key, factory);
        }

        public Task SetAsync(
            string key,
            TCacheItem value,
            HybridCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return Task.CompletedTask;
        }

        private async Task<TCacheItem?> GetOrCreateCoreAsync(string key, Func<Task<TCacheItem>> factory)
        {
            if (_items.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var created = await factory();
            _items[key] = created;
            return created;
        }
    }

    internal sealed class InMemoryDistributedCache<TCacheItem> : IDistributedCache<TCacheItem>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _items = new(StringComparer.Ordinal);

        public TCacheItem? Get(string key, bool? hideErrors = null, bool considerUow = false)
            => _items.GetValueOrDefault(key);

        public KeyValuePair<string, TCacheItem?>[] GetMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
            => keys.Select(key => new KeyValuePair<string, TCacheItem?>(key, Get(key, hideErrors, considerUow))).ToArray();

        public Task<KeyValuePair<string, TCacheItem?>[]> GetManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => Task.FromResult(GetMany(keys, hideErrors, considerUow));

        public Task<TCacheItem?> GetAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => Task.FromResult(Get(key, hideErrors, considerUow));

        public TCacheItem? GetOrAdd(string key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = factory();
            _items[key] = created;
            return created;
        }

        public async Task<TCacheItem?> GetOrAddAsync(string key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = await factory();
            _items[key] = created;
            return created;
        }

        public KeyValuePair<string, TCacheItem?>[] GetOrAddMany(IEnumerable<string> keys, Func<IEnumerable<string>, List<KeyValuePair<string, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
        {
            var missing = keys.Where(key => !_items.ContainsKey(key)).ToList();
            foreach (var pair in factory(missing))
            {
                _items[pair.Key] = pair.Value;
            }

            return GetMany(keys, hideErrors, considerUow);
        }

        public async Task<KeyValuePair<string, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<string> keys, Func<IEnumerable<string>, Task<List<KeyValuePair<string, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            var missing = keys.Where(key => !_items.ContainsKey(key)).ToList();
            foreach (var pair in await factory(missing))
            {
                _items[pair.Key] = pair.Value;
            }

            return GetMany(keys, hideErrors, considerUow);
        }

        public void Set(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
            => _items[key] = value;

        public Task SetAsync(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }

        public void SetMany(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var pair in items)
            {
                _items[pair.Key] = pair.Value;
            }
        }

        public Task SetManyAsync(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            SetMany(items, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void Refresh(string key, bool? hideErrors = null) { }
        public Task RefreshAsync(string key, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void RefreshMany(IEnumerable<string> keys, bool? hideErrors = null) { }
        public Task RefreshManyAsync(IEnumerable<string> keys, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key, bool? hideErrors = null, bool considerUow = false) => _items.Remove(key);
        public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public void RemoveMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }
        }

        public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            RemoveMany(keys, hideErrors, considerUow);
            return Task.CompletedTask;
        }
    }

    internal sealed class NoopIdentityAuthCacheInvalidator : IIdentityAuthCacheInvalidator
    {
        public Task InvalidateAliasesAsync(Guid? tenantId, IEnumerable<string> aliasHashes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateAuthorizationAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateProfileAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateSessionsAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class NoopIdentitySessionRevocationService : IIdentitySessionRevocationService
    {
        public Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> RevokeAllByTenantAsync(Guid tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(IReadOnlyCollection<Guid> sessionIds, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BulkRevokeSessionsResultDto());
    }

    internal sealed class CapturingBackgroundJobManager : IBackgroundJobManager
    {
        public List<CapturedBackgroundJob> Jobs { get; } = [];

        public Task<string> EnqueueAsync<TArgs>(
            TArgs args,
            BackgroundJobEnqueueOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Jobs.Add(new CapturedBackgroundJob(typeof(TArgs), args!, options));
            return Task.FromResult($"test-job-{Jobs.Count}");
        }
    }

    internal sealed record CapturedBackgroundJob(
        Type ArgsType,
        object Args,
        BackgroundJobEnqueueOptions? Options);

    internal sealed class NoopDistributedLock : IDistributedLock
    {
        public Task<IDistributedLockHandle?> TryAcquireAsync(string name, DistributedLockAcquireOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IDistributedLockHandle?>(new NoopDistributedLockHandle(name));

        public Task<IDistributedLockHandle> AcquireAsync(string name, DistributedLockAcquireOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IDistributedLockHandle>(new NoopDistributedLockHandle(name));
    }

    private sealed class NoopDistributedLockHandle : IDistributedLockHandle
    {
        public NoopDistributedLockHandle(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class DisposeAction : IDisposable
    {
        private readonly Action _dispose;

        public DisposeAction(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            _dispose();
        }
    }

    private sealed class EnabledDataFilter : IDataFilter
    {
        private readonly Dictionary<Type, int> _disabledFilters = [];

        public IDisposable Enable<TFilter>() where TFilter : class
        {
            var filterType = typeof(TFilter);
            var previousCount = _disabledFilters.GetValueOrDefault(filterType);
            _disabledFilters.Remove(filterType);
            return new DisposeAction(() => SetDisabledCount(filterType, previousCount));
        }

        public IDisposable Disable<TFilter>() where TFilter : class
        {
            var filterType = typeof(TFilter);
            var previousCount = _disabledFilters.GetValueOrDefault(filterType);
            _disabledFilters[filterType] = previousCount + 1;
            return new DisposeAction(() => SetDisabledCount(filterType, previousCount));
        }

        public bool IsEnabled<TFilter>() where TFilter : class
            => _disabledFilters.GetValueOrDefault(typeof(TFilter)) == 0;

        private void SetDisabledCount(Type filterType, int count)
        {
            if (count == 0)
            {
                _disabledFilters.Remove(filterType);
                return;
            }

            _disabledFilters[filterType] = count;
        }
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object targetObject) { }
        public void SetDeletionProperties(object targetObject) { }
        public void SetModificationProperties(object targetObject) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;

        public IAuditLogSaveHandle BeginScope()
        {
            return new NoopAuditLogScope();
        }

        private sealed class NoopAuditLogScope : IAuditLogScope, IAuditLogSaveHandle
        {
            public AuditLogInfo Log { get; } = new();
            public Task SaveAsync() => Task.CompletedTask;
            public void Dispose() { }
        }
    }

    private sealed class NoopUnitOfWorkManager : Shared.Uow.Abstractions.IUnitOfWorkManager
    {
        public Shared.Uow.Abstractions.IUnitOfWork? Current => null;
        public Shared.Uow.Abstractions.IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class NoopMessageSender : ITenantWrightMessageSender
    {
        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
    }

    private sealed class StaticDbContextProvider<TDbContext>(TDbContext dbContext) : IDbContextProvider<TDbContext>
        where TDbContext : IEfCoreDbContext
    {
        public Task<TDbContext> GetDbContextAsync() => Task.FromResult(dbContext);
    }

    private sealed class NoopUnitOfWork : IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs> Failed { add { } remove { } }
        public event EventHandler<UnitOfWorkEventArgs> Disposed { add { } remove { } }
        public UnitOfWorkOptions Options { get; } = new();
        public IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed => false;
        public bool IsCompleted => false;
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider => null!;
        public void SetOuter(IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) { }
        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
        public IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, IDatabaseApi api) { }
        public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
        public ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, ITransactionApi api) { }
        public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
        public void Dispose() { }
    }
}
