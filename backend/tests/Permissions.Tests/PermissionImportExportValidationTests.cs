using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Xunit;

namespace Permissions.Tests;

public class PermissionImportExportValidationTests
{
    [Fact]
    public void Catalog_csv_export_uses_stable_data_columns_and_escapes_values()
    {
        var csv = PermissionImportExportCsvWriter.WriteCatalog(
        [
            new PermissionCatalogImportItemDto
            {
                Action = "Upsert",
                AllowsWildcard = true,
                DisplayName = "Users, Admins",
                ElementKey = "identity/users",
                ElementType = PermissionElementType.Api,
                IsEnabled = true,
                Name = "Identity.Users",
                ParentName = "Identity",
                SortOrder = 10
            }
        ]);

        Assert.Equal(
            "name,displayName,elementType,elementKey,parentName,isEnabled,allowsWildcard,sortOrder\r\nIdentity.Users,\"Users, Admins\",Api,identity/users,Identity,true,true,10\r\n",
            csv);
    }

    [Fact]
    public void Provider_grant_csv_export_includes_explicit_provider_scope()
    {
        var csv = PermissionImportExportCsvWriter.WriteProviderGrants(
            providerName: "Role",
            providerKey: "admin",
            grants:
            [
                new PermissionProviderGrantImportItemDto
                {
                    Effect = PermissionGrantEffect.Allow,
                    IsGranted = true,
                    Name = "Identity.Users"
                }
            ]);

        Assert.Equal(
            "providerName,providerKey,name,isGranted,effect\r\nRole,admin,Identity.Users,true,Allow\r\n",
            csv);
    }

    [Fact]
    public void Resource_permission_csv_export_includes_resource_and_provider_scope()
    {
        var csv = PermissionImportExportCsvWriter.WriteResourcePermissions(
            resourceName: "Api",
            resourceKey: "identity/users",
            providerName: "User",
            providerKey: "user-1",
            grants:
            [
                new PermissionResourceGrantImportItemDto
                {
                    Action = "Upsert",
                    IsGranted = true,
                    Name = "Identity.Users"
                }
            ]);

        Assert.Equal(
            "resourceName,resourceKey,providerName,providerKey,name,isGranted,action\r\nApi,identity/users,User,user-1,Identity.Users,true,Upsert\r\n",
            csv);
    }

    [Fact]
    public void Catalog_csv_import_parser_maps_export_schema_and_escaped_values()
    {
        const string csv = "name,displayName,elementType,elementKey,parentName,isEnabled,allowsWildcard,sortOrder\r\n" +
                           "Identity.Users,\"Users, Admins\",Api,identity/users,Identity,true,false,20\r\n";

        var parsed = PermissionImportExportCsvParser.ParseCatalog(csv);

        Assert.Empty(parsed.Issues);
        var item = Assert.Single(parsed.Request.Nodes);
        Assert.Equal("Identity.Users", item.Name);
        Assert.Equal("Users, Admins", item.DisplayName);
        Assert.Equal(PermissionElementType.Api, item.ElementType);
        Assert.Equal("identity/users", item.ElementKey);
        Assert.Equal("Identity", item.ParentName);
        Assert.True(item.IsEnabled);
        Assert.False(item.AllowsWildcard);
        Assert.Equal(20, item.SortOrder);
        Assert.Equal("Upsert", item.Action);
    }

    [Fact]
    public void Provider_grant_csv_import_parser_rejects_unsupported_columns_and_unknown_effects()
    {
        const string csv = "providerName,providerKey,name,isGranted,effect,deleteMode\r\n" +
                           "Role,admin,Identity.Users,true,Maybe,delete\r\n";

        var parsed = PermissionImportExportCsvParser.ParseProviderGrants(csv);

        Assert.Contains(parsed.Issues, x => x.Code == "UnsupportedColumn" && x.ItemName == "deleteMode");
        Assert.Contains(parsed.Issues, x => x.Code == "InvalidEffect" && x.ItemName == "Identity.Users");
    }

    [Fact]
    public void Resource_permission_csv_import_parser_rejects_malformed_rows_and_destructive_actions()
    {
        const string csv = "resourceName,resourceKey,providerName,providerKey,name,isGranted,action\r\n" +
                           "Api,identity/users,Role,admin,Identity.Users,true,Delete\r\n" +
                           "Api,identity/users,Role\r\n";

        var parsed = PermissionImportExportCsvParser.ParseResourcePermissions(csv);

        Assert.Single(parsed.Request.Grants);
        Assert.Contains(parsed.Issues, x => x.Code == "UnsupportedAction" && x.ItemName == "Identity.Users");
        Assert.Contains(parsed.Issues, x => x.Code == "MalformedRow" && x.ItemName == "row 3");
    }

    [Fact]
    public void Catalog_validation_reports_duplicate_missing_parent_invalid_type_and_delete_attempts()
    {
        var existingNames = new HashSet<string>(StringComparer.Ordinal) { "Existing.Root" };
        var input = new PermissionCatalogImportRequestDto
        {
            Nodes =
            [
                new PermissionCatalogImportItemDto
                {
                    Name = "Catalog.Items",
                    ElementType = PermissionElementType.Page,
                    ElementKey = "catalog/items",
                    ParentName = "Catalog",
                    Action = "Upsert"
                },
                new PermissionCatalogImportItemDto
                {
                    Name = "Catalog.Items",
                    ElementType = PermissionElementType.Page,
                    ElementKey = "catalog/items-copy",
                    Action = "Upsert"
                },
                new PermissionCatalogImportItemDto
                {
                    Name = "Invalid.Type",
                    ElementType = (PermissionElementType)255,
                    ElementKey = "invalid/type",
                    Action = "Upsert"
                },
                new PermissionCatalogImportItemDto
                {
                    Name = "Delete.Me",
                    ElementType = PermissionElementType.Action,
                    ElementKey = "delete/me",
                    Action = "Delete"
                }
            ]
        };

        var result = PermissionImportExportValidator.ValidateCatalog(input, existingNames);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Code == "DuplicateName" && x.ItemName == "Catalog.Items");
        Assert.Contains(result.Issues, x => x.Code == "MissingParent" && x.ItemName == "Catalog.Items");
        Assert.Contains(result.Issues, x => x.Code == "InvalidElementType" && x.ItemName == "Invalid.Type");
        Assert.Contains(result.Issues, x => x.Code == "UnsupportedAction" && x.ItemName == "Delete.Me");
    }

    [Fact]
    public void Catalog_validation_is_upsert_only_and_does_not_require_omitted_existing_nodes()
    {
        var existingNames = new HashSet<string>(StringComparer.Ordinal) { "Existing.Root", "Existing.Child" };
        var input = new PermissionCatalogImportRequestDto
        {
            Nodes =
            [
                new PermissionCatalogImportItemDto
                {
                    Name = "Existing.Root",
                    ElementType = PermissionElementType.Module,
                    ElementKey = "existing/root",
                    Action = "Upsert"
                }
            ]
        };

        var result = PermissionImportExportValidator.ValidateCatalog(input, existingNames);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.UpsertCount);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Provider_grant_validation_rejects_unknown_permissions_and_unsupported_providers()
    {
        var knownPermissions = new HashSet<string>(StringComparer.Ordinal) { "Known.Permission" };
        var input = new PermissionProviderGrantImportRequestDto
        {
            Grants =
            [
                new PermissionProviderGrantImportItemDto
                {
                    Name = "Unknown.Permission",
                    IsGranted = true,
                    Effect = PermissionGrantEffect.Allow
                }
            ]
        };

        var result = PermissionImportExportValidator.ValidateProviderGrants("Client", "client-a", input, knownPermissions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Code == "UnsupportedProvider");
        Assert.Contains(result.Issues, x => x.Code == "UnknownPermission" && x.ItemName == "Unknown.Permission");
    }

    [Fact]
    public void Provider_grant_validation_accepts_role_and_user_targets_only()
    {
        var knownPermissions = new HashSet<string>(StringComparer.Ordinal) { "Known.Permission" };
        var input = new PermissionProviderGrantImportRequestDto
        {
            Grants =
            [
                new PermissionProviderGrantImportItemDto
                {
                    Name = "Known.Permission",
                    IsGranted = true,
                    Effect = PermissionGrantEffect.Allow
                }
            ]
        };

        var roleResult = PermissionImportExportValidator.ValidateProviderGrants("Role", Guid.NewGuid().ToString("D"), input, knownPermissions);
        var userResult = PermissionImportExportValidator.ValidateProviderGrants("User", Guid.NewGuid().ToString("D"), input, knownPermissions);

        Assert.True(roleResult.IsValid);
        Assert.True(userResult.IsValid);
    }

    [Fact]
    public void Resource_permission_validation_rejects_unsupported_provider_unknown_permission_and_missing_resource()
    {
        var knownPermissions = new HashSet<string>(StringComparer.Ordinal) { "Known.Permission" };
        var knownResources = new HashSet<string>(StringComparer.Ordinal) { "Page:documents/list" };
        var input = new PermissionResourceGrantImportRequestDto
        {
            ResourceName = "Page",
            ResourceKey = "missing/resource",
            Grants =
            [
                new PermissionResourceGrantImportItemDto
                {
                    Action = "Delete",
                    Name = "Unknown.Permission",
                    IsGranted = true
                }
            ]
        };

        var result = PermissionImportExportValidator.ValidateResourcePermissions(
            "Client",
            "client-a",
            input,
            knownPermissions,
            knownResources);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Code == "UnsupportedProvider");
        Assert.Contains(result.Issues, x => x.Code == "UnknownPermission" && x.ItemName == "Unknown.Permission");
        Assert.Contains(result.Issues, x => x.Code == "UnknownResource" && x.ItemName == "Page:missing/resource");
        Assert.Contains(result.Issues, x => x.Code == "UnsupportedAction" && x.ItemName == "Unknown.Permission");
    }

    [Fact]
    public void Resource_permission_validation_accepts_catalog_backed_role_and_user_targets()
    {
        var knownPermissions = new HashSet<string>(StringComparer.Ordinal) { "Known.Permission" };
        var knownResources = new HashSet<string>(StringComparer.Ordinal) { "Api:identity/users" };
        var input = new PermissionResourceGrantImportRequestDto
        {
            ResourceName = "Api",
            ResourceKey = "identity/users",
            Grants =
            [
                new PermissionResourceGrantImportItemDto
                {
                    Action = "Upsert",
                    Name = "Known.Permission",
                    IsGranted = true
                }
            ]
        };

        var roleResult = PermissionImportExportValidator.ValidateResourcePermissions(
            "Role",
            Guid.NewGuid().ToString("D"),
            input,
            knownPermissions,
            knownResources);
        var userResult = PermissionImportExportValidator.ValidateResourcePermissions(
            "User",
            Guid.NewGuid().ToString("D"),
            input,
            knownPermissions,
            knownResources);

        Assert.True(roleResult.IsValid);
        Assert.True(userResult.IsValid);
        Assert.Equal(1, roleResult.UpsertCount);
    }
}
