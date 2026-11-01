using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Identity.Application.ClaimTypes;
using Identity.Application.Contracts.ClaimTypes;
using Identity.Application.Contracts.Invitations;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.SecurityLogs;
using Identity.Application.Contracts.Users;
using Identity.Application.Invitations;
using Identity.Application.Platform;
using Identity.Application.SecurityLogs;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Binds every list input's <see cref="SortableFieldsAttribute"/> to the query mapping that serves it.
/// </summary>
/// <remarks>
/// <para>
/// The attribute is the whitelist; a hand-written <c>switch</c> turns an accepted value into an
/// <c>OrderBy</c>. Nothing in the compiler keeps the two in step, and when they drift the failure is
/// silent: an unmapped-but-declared field falls through to <c>_ =&gt;</c> and the caller receives a
/// correctly-paged list in the wrong order. That is the defect this suite exists to catch — several of
/// these mappings shipped direction-incomplete, accepting <c>"email"</c> but not <c>"email asc"</c>.
/// </para>
/// <para>
/// Assertions are made against the LINQ expression tree rather than query results, so no seeded data or
/// database round-trip is needed. One limitation is deliberate: where a field/direction pair produces
/// exactly the mapping's default order, a missing arm is indistinguishable from a present one — the
/// observable behaviour is identical either way.
/// </para>
/// </remarks>
public class SortableFieldsMappingTests
{
    /// <summary>A substring that must appear in the ordering expression for a given declared field.</summary>
    private static readonly Dictionary<string, string> ApplicationUserMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserName"] = "UserName",
        ["Email"] = "Email",
        ["IsActive"] = "IsActive",
        ["Created"] = "CreatedAtUtc",
        // LastLogin aggregates over the security log rather than reading a column.
        ["LastLogin"] = "Max(",
    };

    [Fact]
    public void GetUsersInput_declares_only_fields_UserAppService_can_order_by()
    {
        using var dbContext = CreateInMemoryDbContext();

        AssertEveryDeclaredFieldIsMapped<GetUsersInput>(
            ApplicationUserMarkers,
            sorting => OrderingExpression(
                typeof(UserAppService),
                "ApplySorting",
                Empty<ApplicationUser>(),
                sorting,
                dbContext));
    }

    [Fact]
    public void GetPlatformStaffInput_declares_only_fields_PlatformStaffAppService_can_order_by()
    {
        using var dbContext = CreateInMemoryDbContext();

        AssertEveryDeclaredFieldIsMapped<GetPlatformStaffInput>(
            ApplicationUserMarkers,
            sorting => OrderingExpression(
                typeof(PlatformStaffAppService),
                "ApplySorting",
                Empty<ApplicationUser>(),
                sorting,
                dbContext));
    }

    [Fact]
    public void GetHostTenantUsersInput_inherits_the_declaration_it_is_sorted_by()
    {
        // GetHostTenantUsersInput derives from GetUsersInput and is served by the same mapping, so the
        // inherited declaration must resolve rather than silently reading as "unsortable".
        var declaration = SortingValidator.GetDeclaration<GetHostTenantUsersInput>();

        Assert.NotNull(declaration);
        Assert.Equal(
            SortingValidator.GetDeclaration<GetUsersInput>()!.Fields,
            declaration!.Fields);
    }

    [Fact]
    public void GetUserSessionsPagedInput_declares_only_fields_the_session_mapping_can_order_by()
    {
        AssertEveryDeclaredFieldIsMapped<GetUserSessionsPagedInput>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CreationTime"] = "CreationTime",
                ["LastActiveTime"] = "LastActiveTime",
                ["Status"] = "Status",
                ["RevokedAtUtc"] = "RevokedAtUtc",
                ["IpAddress"] = "IpAddress",
            },
            sorting => OrderingExpression(
                typeof(IdentitySessionAppService),
                "ApplySorting",
                Empty<IdentitySession>(),
                sorting));
    }

    [Fact]
    public void GetOperationalSessionsInput_declares_only_fields_the_operational_mapping_can_order_by()
    {
        AssertEveryDeclaredFieldIsMapped<GetOperationalSessionsInput>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CreationTime"] = "CreationTime",
                ["LastActiveTime"] = "LastActiveTime",
                ["Status"] = "Status",
                // The operational row carries the tenant identifier, not its display name.
                ["TenantName"] = "TenantId",
                ["UserName"] = "UserName",
                ["Email"] = "Email",
                ["RevokedAtUtc"] = "RevokedAtUtc",
            },
            sorting => OrderingExpression(
                typeof(IdentitySessionAppService),
                "ApplyOperationalSorting",
                EmptyOperationalSessionRows(),
                sorting));
    }

    [Theory]
    [InlineData(typeof(InvitationListInput))]
    [InlineData(typeof(PlatformStaffInvitationListInput))]
    public void Invitation_inputs_declare_only_fields_the_invitation_mapping_can_order_by(Type inputType)
    {
        // Both inputs share InvitationAppService.ApplySorting, so both must declare the same set.
        AssertEveryDeclaredFieldIsMapped(
            inputType,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Email"] = "Email",
                ["ExpiresAtUtc"] = "ExpiresAtUtc",
                ["CreatedAtUtc"] = "CreatedAtUtc",
            },
            sorting => OrderingExpression(
                typeof(InvitationAppService),
                "ApplySorting",
                Empty<IdentityPublicInvitation>(),
                sorting));
    }

    [Fact]
    public void SecurityLogListInput_declares_only_fields_the_security_log_mapping_can_order_by()
    {
        AssertEveryDeclaredFieldIsMapped<SecurityLogListInput>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CreationTime"] = "CreationTime",
                ["UserName"] = "UserName",
                ["Action"] = "Action",
                ["RiskScore"] = "RiskScore",
            },
            sorting => OrderingExpression(
                typeof(SecurityLogQueryExtensions),
                "ApplySorting",
                Empty<IdentitySecurityLog>(),
                sorting));
    }

    [Theory]
    [InlineData(typeof(Identity.Controllers.Platform.PlatformRolesController), typeof(PlatformRoleListInput))]
    [InlineData(typeof(Identity.Controllers.ClaimTypes.ClaimTypesController), typeof(ClaimTypeListInput))]
    [InlineData(typeof(Identity.Controllers.ClaimTypes.PlatformClaimTypesController), typeof(ClaimTypeListInput))]
    public void The_list_action_takes_the_input_that_carries_the_declaration(
        Type controller,
        Type expectedInput)
    {
        // SharedSortableFieldsOperationTransformer publishes x-tenantwright-sortable-fields by reading the
        // action's parameter types. Rebinding these actions to loose parameters instead of the input DTO
        // would still compile and still sort, but the declaration would vanish from the OpenAPI document
        // and every generated client would lose the sortable set without any failure.
        var parameters = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == "GetListAsync")
            .GetParameters();

        Assert.Contains(expectedInput, parameters.Select(parameter => parameter.ParameterType));
        Assert.NotNull(SortingValidator.GetDeclaration(expectedInput));
    }

    [Fact]
    public void PlatformRoleListInput_declares_only_fields_the_role_mapping_can_order_by()
    {
        AssertEveryDeclaredFieldIsMapped<PlatformRoleListInput>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "Name",
                ["Category"] = "Category",
                ["LifecycleState"] = "LifecycleState",
            },
            sorting => OrderingExpression(
                typeof(PlatformRoleAppService),
                "ApplySorting",
                Empty<ApplicationRole>(),
                sorting));
    }

    [Fact]
    public void PlatformRoleListInput_does_not_declare_the_member_count_it_cannot_order_by()
    {
        // MemberCount is on the row but comes from a separate grouped query, so it is not orderable here.
        // Declaring it would accept a sort the mapping cannot produce and answer in name order instead.
        var declaration = SortingValidator.GetDeclaration<PlatformRoleListInput>();

        Assert.DoesNotContain(
            "MemberCount",
            declaration!.Fields,
            StringComparer.OrdinalIgnoreCase);

        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<PlatformRoleListInput>("MemberCount"));
        Assert.True(exception.Errors!.ContainsKey("sorting"));
    }

    [Fact]
    public void ClaimTypeListInput_declares_only_fields_the_claim_type_mapping_can_order_by()
    {
        AssertEveryDeclaredFieldIsMapped<ClaimTypeListInput>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "Name",
                ["ValueType"] = "ValueType",
                ["Required"] = "Required",
            },
            sorting => OrderingExpression(
                typeof(IdentityClaimTypeAppService),
                "ApplySorting",
                Empty<IdentityClaimType>(),
                sorting));
    }

    [Fact]
    public void Every_declared_default_sort_is_itself_an_accepted_value()
    {
        // A default the validator would reject would fail every unsorted request, so it is checked
        // against the same whitelist a caller is held to.
        Type[] inputs =
        [
            typeof(GetUsersInput),
            typeof(GetPlatformStaffInput),
            typeof(GetUserSessionsPagedInput),
            typeof(GetOperationalSessionsInput),
            typeof(InvitationListInput),
            typeof(PlatformStaffInvitationListInput),
            typeof(SecurityLogListInput),
            typeof(PlatformRoleListInput),
            typeof(ClaimTypeListInput),
        ];

        foreach (var input in inputs)
        {
            var declaration = SortingValidator.GetDeclaration(input);
            Assert.NotNull(declaration);

            if (declaration!.DefaultSort is null)
            {
                continue;
            }

            Assert.Equal(
                declaration.DefaultSort,
                SortingValidator.ValidateAndNormalize(input, declaration.DefaultSort));
        }
    }

    private static void AssertEveryDeclaredFieldIsMapped<TInput>(
        IReadOnlyDictionary<string, string> markers,
        Func<string?, string> orderingExpression) =>
        AssertEveryDeclaredFieldIsMapped(typeof(TInput), markers, orderingExpression);

    private static void AssertEveryDeclaredFieldIsMapped(
        Type inputType,
        IReadOnlyDictionary<string, string> markers,
        Func<string?, string> orderingExpression)
    {
        var declaration = SortingValidator.GetDeclaration(inputType);
        Assert.NotNull(declaration);

        // A field the test does not describe would pass vacuously, so the two lists must agree.
        Assert.Equal(
            declaration!.Fields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase),
            markers.Keys.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));

        foreach (var field in declaration.Fields)
        {
            var marker = markers[field];

            var ascending = orderingExpression(
                SortingValidator.ValidateAndNormalize(inputType, $"{field} asc"));
            var descending = orderingExpression(
                SortingValidator.ValidateAndNormalize(inputType, $"{field} desc"));
            var bare = orderingExpression(
                SortingValidator.ValidateAndNormalize(inputType, field));

            Assert.Contains(marker, ascending, StringComparison.Ordinal);
            Assert.Contains(marker, descending, StringComparison.Ordinal);

            // ".OrderBy(" cannot match inside "OrderByDescending(", so these two are exclusive.
            Assert.Contains(".OrderBy(", ascending, StringComparison.Ordinal);
            Assert.Contains(".OrderByDescending(", descending, StringComparison.Ordinal);

            Assert.NotEqual(ascending, descending);

            // A bare field name means ascending everywhere. Identity previously gave "created" and
            // "lastlogin" a descending meaning while "email" meant ascending.
            Assert.Equal(ascending, bare);
        }
    }

    private static string OrderingExpression(
        Type declaringType,
        string methodName,
        object source,
        string? sorting,
        object? dbContext = null)
    {
        var parameterCount = dbContext is null ? 2 : 3;
        var method = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == methodName
                && candidate.GetParameters().Length == parameterCount);

        var arguments = dbContext is null
            ? new object?[] { source, sorting }
            : new object?[] { dbContext, source, sorting };

        var ordered = (IQueryable)method.Invoke(null, arguments)!;
        return ordered.Expression.ToString();
    }

    private static IQueryable<TEntity> Empty<TEntity>() =>
        Array.Empty<TEntity>().AsQueryable();

    /// <summary>
    /// The operational query projects into a private nested row type, so it is resolved reflectively
    /// rather than named.
    /// </summary>
    private static object EmptyOperationalSessionRows()
    {
        var rowType = typeof(IdentitySessionAppService)
            .GetNestedType("OperationalSessionQueryRow", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "IdentitySessionAppService.OperationalSessionQueryRow was renamed or removed; "
                + "update this test to match.");

        return typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == nameof(Queryable.AsQueryable)
                && candidate.IsGenericMethodDefinition)
            .MakeGenericMethod(rowType)
            .Invoke(null, [Array.CreateInstance(rowType, 0)])!;
    }

    private static IdentityDbContextApp CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new IdentityDbContextApp(options);
    }
}
