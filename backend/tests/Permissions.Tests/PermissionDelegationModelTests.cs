using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Permissions.Domain.Shared.Enums;
using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Tests.TestDoubles;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionDelegationModelTests
{
    [Fact]
    public void Model_maps_server_owned_delegation_descriptors_and_current_version_ownership()
    {
        using var connection = OpenDatabase();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);
        Assert.True(context.Database.EnsureCreated());
        var model = context.GetService<IDesignTimeModel>().Model;
        var definition = model.FindEntityType(typeof(PermissionDelegationDefinition))!;
        var version = model.FindEntityType(typeof(PermissionDelegationDefinitionVersion))!;

        Assert.Equal("char(64)", version.FindProperty(nameof(PermissionDelegationDefinitionVersion.SourceDefinitionChecksum))!.GetColumnType());
        Assert.Equal("smallint", version.FindProperty(nameof(PermissionDelegationDefinitionVersion.AllowedScopeKinds))!.GetColumnType());
        Assert.Contains(definition.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { nameof(PermissionDelegationDefinition.PermissionName) }));
        Assert.Contains(version.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { nameof(PermissionDelegationDefinitionVersion.DefinitionId), nameof(PermissionDelegationDefinitionVersion.Version) }));
        Assert.Contains(version.GetIndexes(), index => index.IsUnique && index.GetFilter() == "\"PublicationState\" = 2");
        Assert.Contains(version.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(PermissionDelegationDefinition) &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(PermissionDelegationDefinitionVersion.DefinitionId) }));
        Assert.Contains(definition.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(PermissionDelegationDefinitionVersion) &&
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(PermissionDelegationDefinition.Id), nameof(PermissionDelegationDefinition.CurrentVersionId) }));
        Assert.Contains(version.GetCheckConstraints(), constraint => constraint.Name == "CK_PermissionDelegationDefinitionVersions_Approval");
        Assert.Contains(version.GetCheckConstraints(), constraint => constraint.Name == "CK_PermissionDelegationDefinitionVersions_Flags");
    }

    [Fact]
    public void Model_extends_existing_resource_tables_with_complete_owner_validation_fences()
    {
        using var connection = OpenDatabase();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);
        var model = context.GetService<IDesignTimeModel>().Model;
        var grant = model.FindEntityType(typeof(ResourcePermissionGrant))!;
        var state = model.FindEntityType(typeof(ResourcePermissionAssignmentState))!;
        var operation = model.FindEntityType(typeof(ResourcePermissionAssignmentOperation))!;
        var request = model.FindEntityType(typeof(PermissionGrantChangeRequest))!;
        var decision = model.FindEntityType(typeof(PermissionApprovalDecisionOperation))!;
        var history = model.FindEntityType(typeof(PermissionGrantHistoryEntry))!;

        Assert.Equal("smallint", grant.FindProperty(nameof(ResourcePermissionGrant.ResourceScopeKind))!.GetColumnType());
        Assert.Equal("timestamp with time zone", grant.FindProperty(nameof(ResourcePermissionGrant.OwnerValidationReceiptExpiresAtUtc))!.GetColumnType());
        Assert.Contains(grant.GetCheckConstraints(), constraint => constraint.Name == "CK_ResourcePermissionGrants_ResourceScope");
        Assert.Contains(grant.GetCheckConstraints(), constraint => constraint.Name == "CK_ResourcePermissionGrants_Review");
        Assert.Contains(state.GetCheckConstraints(), constraint => constraint.Name == "CK_ResourcePermissionAssignmentStates_OwnerValidation");
        Assert.Contains(operation.GetCheckConstraints(), constraint => constraint.Name == "CK_ResourcePermissionAssignmentOperations_OwnerValidation");
        Assert.Contains(request.GetCheckConstraints(), constraint => constraint.Name == "CK_PermissionGrantChangeRequests_ResourceScope");
        Assert.Contains(decision.GetCheckConstraints(), constraint => constraint.Name == "CK_PermissionApprovalDecisionOperations_ResourceScope");
        Assert.Contains(history.GetCheckConstraints(), constraint => constraint.Name == "CK_PermissionGrantHistoryEntries_ResourceScope");
        Assert.Contains(state.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[]
        {
            nameof(ResourcePermissionAssignmentState.TenantId),
            nameof(ResourcePermissionAssignmentState.ResourceScopeKind),
            nameof(ResourcePermissionAssignmentState.ResourceName),
            nameof(ResourcePermissionAssignmentState.ResourceKey),
            nameof(ResourcePermissionAssignmentState.ProviderName),
            nameof(ResourcePermissionAssignmentState.ProviderKey)
        }));
        Assert.Contains(operation.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[]
        {
            nameof(ResourcePermissionAssignmentOperation.TenantId),
            nameof(ResourcePermissionAssignmentOperation.ActorUserId),
            nameof(ResourcePermissionAssignmentOperation.ResourceScopeKind),
            nameof(ResourcePermissionAssignmentOperation.ResourceName),
            nameof(ResourcePermissionAssignmentOperation.ResourceKey),
            nameof(ResourcePermissionAssignmentOperation.ProviderName),
            nameof(ResourcePermissionAssignmentOperation.ProviderKey),
            nameof(ResourcePermissionAssignmentOperation.IdempotencyKey)
        }));
        Assert.True(state.FindProperty(nameof(ResourcePermissionAssignmentState.Version))!.IsConcurrencyToken);
        Assert.Contains(request.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(PermissionDelegationDefinitionVersion));
        Assert.Contains(decision.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(PermissionDelegationDefinitionVersion));
        Assert.Contains(history.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(PermissionGrantChangeRequest));
    }

    [Fact]
    public void Current_version_foreign_key_rejects_a_version_from_a_different_definition()
    {
        using var connection = OpenDatabase();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null);
        context.Database.EnsureCreated();

        var firstDefinition = new PermissionDelegationDefinition(Guid.CreateVersion7(), "Workspaces.Documents.Read", "Workspaces");
        var secondDefinition = new PermissionDelegationDefinition(Guid.CreateVersion7(), "Workspaces.Queues.Read", "Workspaces");
        context.PermissionDelegationDefinitions.AddRange(firstDefinition, secondDefinition);
        context.SaveChanges();

        var secondVersion = new PermissionDelegationDefinitionVersion(
            Guid.CreateVersion7(),
            secondDefinition.Id,
            1,
            new string('a', 64),
            "Workspaces.Manage",
            PermissionDelegationTargetKinds.User,
            PermissionDelegationScopeKinds.Resource,
            PermissionDelegationEffects.Allow,
            maximumExpirySeconds: null,
            requiresApproval: false,
            approverPermissionName: null,
            reviewCadenceDays: null,
            requiredFeatureName: null,
            delegable: false,
            effectiveFromUtc: DateTime.UtcNow,
            effectiveToUtc: null);
        context.PermissionDelegationDefinitionVersions.Add(secondVersion);
        context.SaveChanges();

        firstDefinition.Publish(secondVersion.Id);

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        return connection;
    }
}
