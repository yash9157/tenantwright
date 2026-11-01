using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Definitions;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.PermissionManagement;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Tests.TestDoubles;
using Shared.Domain.Repositories;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionDelegationDefinitionSyncTests
{
    [Fact]
    public async Task SyncAsync_delegation_descriptor_lifecycle_should_project_idempotently_version_immutable_changes_and_retire_omissions()
    {
        using var connection = OpenDatabase();
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);
        var initial = TimeSpan.FromSeconds(60);

        await SyncAsync(context, initial, includeDelegation: true);
        var initialDefinition = await context.PermissionDelegationDefinitions.SingleAsync();
        var initialVersion = await context.PermissionDelegationDefinitionVersions.SingleAsync();
        var initialDefinitionId = initialDefinition.Id;
        var initialVersionId = initialVersion.Id;

        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionLifecycle.Active, initialDefinition.Lifecycle);
        Assert.Equal(initialVersionId, initialDefinition.CurrentVersionId);
        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionPublicationState.Published, initialVersion.PublicationState);
        Assert.Equal(CanonicalChecksum(initial), initialVersion.SourceDefinitionChecksum);
        Assert.Equal(60, initialVersion.MaximumExpirySeconds);

        context.ChangeTracker.Clear();
        await SyncAsync(context, initial, includeDelegation: true);
        var repeatedDefinitions = await context.PermissionDelegationDefinitions.ToListAsync();
        var repeatedVersions = await context.PermissionDelegationDefinitionVersions.ToListAsync();
        Assert.Single(repeatedDefinitions);
        Assert.Single(repeatedVersions);
        Assert.Equal(initialDefinitionId, repeatedDefinitions[0].Id);
        Assert.Equal(initialVersionId, repeatedVersions[0].Id);

        context.ChangeTracker.Clear();
        var changed = TimeSpan.FromSeconds(120);
        await SyncAsync(context, changed, includeDelegation: true);
        var changedDefinition = await context.PermissionDelegationDefinitions.SingleAsync();
        var versionsAfterChange = await context.PermissionDelegationDefinitionVersions.OrderBy(version => version.Version).ToListAsync();
        Assert.Equal(initialDefinitionId, changedDefinition.Id);
        Assert.Equal(2, versionsAfterChange.Count);
        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionPublicationState.Retired, versionsAfterChange[0].PublicationState);
        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionPublicationState.Published, versionsAfterChange[1].PublicationState);
        Assert.Equal(2, versionsAfterChange[1].Version);
        Assert.Equal(versionsAfterChange[1].Id, changedDefinition.CurrentVersionId);
        Assert.Equal(CanonicalChecksum(changed), versionsAfterChange[1].SourceDefinitionChecksum);
        Assert.Equal(120, versionsAfterChange[1].MaximumExpirySeconds);

        context.ChangeTracker.Clear();
        await SyncAsync(context, changed, includeDelegation: false);
        var retiredDefinition = await context.PermissionDelegationDefinitions.SingleAsync();
        var retiredVersions = await context.PermissionDelegationDefinitionVersions.OrderBy(version => version.Version).ToListAsync();
        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionLifecycle.Retired, retiredDefinition.Lifecycle);
        Assert.Equal(versionsAfterChange[1].Id, retiredDefinition.CurrentVersionId);
        Assert.All(retiredVersions, version => Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionPublicationState.Retired, version.PublicationState));
    }

    [Fact]
    public async Task SyncAsync_disabled_delegation_descriptor_should_retire_its_active_definition_and_current_version()
    {
        using var connection = OpenDatabase();
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);

        await SyncAsync(context, TimeSpan.FromSeconds(60), includeDelegation: true);
        context.ChangeTracker.Clear();
        await SyncAsync(context, TimeSpan.FromSeconds(60), includeDelegation: true, isEnabled: false);

        var definition = await context.PermissionDelegationDefinitions.SingleAsync();
        var version = await context.PermissionDelegationDefinitionVersions.SingleAsync();
        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionLifecycle.Retired, definition.Lifecycle);
        Assert.Equal(version.Id, definition.CurrentVersionId);
        Assert.Equal(Permissions.Domain.Shared.Enums.PermissionDelegationDefinitionPublicationState.Retired, version.PublicationState);
    }

    private static async Task SyncAsync(PermissionsDbContextApp context, TimeSpan maximumExpiry, bool includeDelegation, bool isEnabled = true)
    {
        var permissionDefinitions = new PermissionDefinitionManager(
            [new DelegatedDefinitionProvider(maximumExpiry, includeDelegation, isEnabled)]);
        var service = new PermissionDefinitionSyncService(
            permissionDefinitions,
            new FeatureDefinitionManager(Array.Empty<FeatureDefinitionProvider>()),
            Repository<PermissionGroupDefinitionRecord>(context),
            Repository<PermissionDefinitionRecord>(context),
            Repository<FeatureGroupDefinitionRecord>(context),
            Repository<FeatureDefinitionRecord>(context),
            Repository<PermissionDelegationDefinition>(context),
            Repository<PermissionDelegationDefinitionVersion>(context));

        await service.SyncAsync();
        await context.SaveChangesAsync();
    }

    private static EfTestRepository<TEntity, Guid> Repository<TEntity>(PermissionsDbContextApp context)
        where TEntity : class, Shared.Domain.Entities.IEntity<Guid> => new EfTestRepository<TEntity, Guid>(context);

    private static string CanonicalChecksum(TimeSpan maximumExpiry)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            PermissionName = "Workspaces.CheckIn",
            IsEnabled = true,
            OwningService = "Workspaces",
            ManagerPermissionName = "Workspaces.ManageAccess",
            ApproverPermissionName = (string?)null,
            AllowedTargetKinds = (short)(PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role),
            AllowedScopeKinds = (short)PermissionDelegationScopeKinds.Resource,
            AllowedEffects = (short)(PermissionDelegationEffects.Allow | PermissionDelegationEffects.Deny),
            MaximumExpirySeconds = maximumExpiry.TotalSeconds,
            RequiresApproval = false,
            ReviewCadenceDays = (double?)null,
            RequiredFeatureName = (string?)null,
            Delegable = true
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);
        context.Database.EnsureCreated();
        return connection;
    }

    private sealed class DelegatedDefinitionProvider : PermissionDefinitionProvider
    {
        private readonly TimeSpan _maximumExpiry;
        private readonly bool _includeDelegation;
        private readonly bool _isEnabled;
        private readonly bool _isTestConfigured;

        // Permission-definition discovery scans loaded test assemblies in composition tests. This inert
        // constructor keeps the test-only provider from becoming an accidental catalog contributor.
        public DelegatedDefinitionProvider()
        {
        }

        public DelegatedDefinitionProvider(TimeSpan maximumExpiry, bool includeDelegation, bool isEnabled)
        {
            _maximumExpiry = maximumExpiry;
            _includeDelegation = includeDelegation;
            _isEnabled = isEnabled;
            _isTestConfigured = true;
        }

        public override void Define(IPermissionDefinitionContext context)
        {
            if (!_isTestConfigured)
                return;

            var permission = context.AddGroup("Workspaces", "Workspaces")
                .AddPermission("Workspaces.CheckIn", "Check in");
            permission.IsEnabled = _isEnabled;
            if (!_includeDelegation)
                return;

            permission.ConfigureDelegation(new PermissionDelegationMetadata(
                owningService: "Workspaces",
                managerPermissionName: "Workspaces.ManageAccess",
                approverPermissionName: null,
                allowedTargetKinds: PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role,
                allowedScopeKinds: PermissionDelegationScopeKinds.Resource,
                allowedEffects: PermissionDelegationEffects.Allow | PermissionDelegationEffects.Deny,
                maximumExpiry: _maximumExpiry,
                requiresApproval: false,
                reviewCadence: null,
                requiredFeatureName: null,
                delegable: true));
        }
    }
}
