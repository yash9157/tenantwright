using Microsoft.EntityFrameworkCore;
using Permissions.Domain.Catalog;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.Features;
using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Modeling;

namespace Permissions.EntityFrameworkCore.EntityFrameworkCore;

[ConnectionStringName(TenantWrightDatabaseCatalog.Permissions)]
public class PermissionsDbContextApp : SharedDbContext<PermissionsDbContextApp>
{

    public DbSet<PermissionGrant> PermissionGrants { get; set; } = default!;
    public DbSet<ResourcePermissionGrant> ResourcePermissionGrants { get; set; } = default!;
    public DbSet<PermissionConflictRule> PermissionConflictRules { get; set; } = default!;
    public DbSet<PermissionConflictRuleChangeRequest> PermissionConflictRuleChangeRequests { get; set; } = default!;
    public DbSet<Permissions.Domain.ImportExport.PermissionImportJob> PermissionImportJobs { get; set; } = default!;
    public DbSet<PermissionGrantChangeRequest> PermissionGrantChangeRequests { get; set; } = default!;
    public DbSet<PermissionApprovalDecisionOperation> PermissionApprovalDecisionOperations { get; set; } = default!;
    public DbSet<PermissionGrantHistoryEntry> PermissionGrantHistoryEntries { get; set; } = default!;
    public DbSet<PermissionAssignmentState> PermissionAssignmentStates { get; set; } = default!;
    public DbSet<PermissionAssignmentOperation> PermissionAssignmentOperations { get; set; } = default!;
    public DbSet<ResourcePermissionAssignmentState> ResourcePermissionAssignmentStates { get; set; } = default!;
    public DbSet<ResourcePermissionAssignmentOperation> ResourcePermissionAssignmentOperations { get; set; } = default!;
    public DbSet<AccessReviewCampaign> AccessReviewCampaigns { get; set; } = default!;
    public DbSet<AccessReviewItem> AccessReviewItems { get; set; } = default!;
    public DbSet<IdentityPlatformRoleProjection> IdentityPlatformRoleProjections { get; set; } = default!;
    public DbSet<PermissionNode> PermissionNodes { get; set; } = default!;
    public DbSet<PermissionDelegationDefinition> PermissionDelegationDefinitions { get; set; } = default!;
    public DbSet<PermissionDelegationDefinitionVersion> PermissionDelegationDefinitionVersions { get; set; } = default!;
    public DbSet<PermissionFeatureBinding> PermissionFeatureBindings { get; set; } = default!;
    public DbSet<PermissionGroupDefinitionRecord> PermissionGroups { get; set; } = default!;
    public DbSet<PermissionDefinitionRecord> Permissions { get; set; } = default!;
    public DbSet<FeatureGroupDefinitionRecord> FeatureGroups { get; set; } = default!;
    public DbSet<FeatureDefinitionRecord> Features { get; set; } = default!;
    public DbSet<FeatureValue> FeatureValues { get; set; } = default!;
    public DbSet<FeatureProviderState> FeatureProviderStates { get; set; } = default!;
    public DbSet<TenantEntitlementProvisioningReceipt> TenantEntitlementProvisioningReceipts { get; set; } = default!;

    public PermissionsDbContextApp(DbContextOptions<PermissionsDbContextApp> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddTenantWrightMessagingEntities();
        modelBuilder.ConfigurePermissions();
    }
}
