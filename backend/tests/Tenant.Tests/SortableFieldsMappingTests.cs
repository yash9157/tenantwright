using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shared;
using Tenant.Application.Contracts.Tenants;
using Tenant.Application.Tenants;
using Xunit;

namespace Tenant.Tests;

/// <summary>
/// Binds <see cref="GetTenantsInput"/>'s <see cref="SortableFieldsAttribute"/> to the mapping in
/// <c>TenantAppService.ApplySort</c> that serves it.
/// </summary>
/// <remarks>
/// The attribute is the whitelist; a hand-written <c>switch</c> turns an accepted value into an
/// <c>OrderBy</c>. Nothing in the compiler keeps them in step, and drift fails silently: a
/// declared-but-unmapped field falls through to <c>_ =&gt;</c> and the caller receives a correctly-paged
/// list in the wrong order.
/// </remarks>
public class SortableFieldsMappingTests
{
    /// <summary>A substring that must appear in the ordering expression for each declared field.</summary>
    private static readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = "Name",
        ["CreationTime"] = "CreationTime",
        ["LifecycleStatus"] = "LifecycleStatus",
        // Ordered by the denormalized owner display name rather than a column called "Owner".
        ["Owner"] = "OwnerDisplayName",
    };

    [Fact]
    public void GetTenantsInput_declares_only_fields_the_tenant_list_mapping_can_order_by()
    {
        var declaration = SortingValidator.GetDeclaration<GetTenantsInput>();
        Assert.NotNull(declaration);

        // A field the test does not describe would pass vacuously, so the two lists must agree.
        Assert.Equal(
            declaration!.Fields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase),
            Markers.Keys.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));

        foreach (var field in declaration.Fields)
        {
            var marker = Markers[field];

            var ascending = OrderingExpression($"{field} asc");
            var descending = OrderingExpression($"{field} desc");
            var bare = OrderingExpression(field);

            Assert.Contains(marker, ascending, StringComparison.Ordinal);
            Assert.Contains(marker, descending, StringComparison.Ordinal);

            // ".OrderBy(" cannot match inside "OrderByDescending(", so these two are exclusive.
            Assert.Contains(".OrderBy(", ascending, StringComparison.Ordinal);
            Assert.Contains(".OrderByDescending(", descending, StringComparison.Ordinal);

            Assert.NotEqual(ascending, descending);

            // A bare field name means ascending. The retired TenantListSortField enum carried the
            // direction in the member name, so this convention is new and worth pinning.
            Assert.Equal(ascending, bare);
        }
    }

    [Fact]
    public void Every_sort_keeps_a_deterministic_tiebreaker()
    {
        var declaration = SortingValidator.GetDeclaration<GetTenantsInput>();

        foreach (var field in declaration!.Fields)
        {
            foreach (var sorting in new[] { field, $"{field} asc", $"{field} desc" })
            {
                // Offset paging repeats or drops rows without a total order, so Id must always be part
                // of the ordering.
                Assert.Contains("Id", OrderingExpression(sorting), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_declared_default_sort_is_itself_an_accepted_value()
    {
        var declaration = SortingValidator.GetDeclaration<GetTenantsInput>();
        Assert.NotNull(declaration);
        Assert.Equal("Name", declaration!.DefaultSort);

        // A default the validator would reject would fail every unsorted request.
        Assert.Equal(
            declaration.DefaultSort,
            SortingValidator.ValidateAndNormalize<GetTenantsInput>(declaration.DefaultSort));
    }

    [Fact]
    public void An_undeclared_field_is_refused_rather_than_reinterpreted()
    {
        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<GetTenantsInput>("ConnectionString"));

        Assert.NotNull(exception.Errors);
        Assert.True(exception.Errors!.ContainsKey("sorting"));
    }

    private static string OrderingExpression(string? sorting)
    {
        var validated = SortingValidator.ValidateAndNormalize<GetTenantsInput>(sorting);

        var method = typeof(TenantAppService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == "ApplySort"
                && candidate.GetParameters().Length == 2);

        var source = Array.Empty<Tenant.Domain.Tenants.Tenant>().AsQueryable();
        var ordered = (IQueryable)method.Invoke(null, [source, validated])!;
        return ordered.Expression.ToString();
    }
}
