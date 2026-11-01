using System;
using System.Collections.Generic;
using Xunit;

namespace Shared.Modularity.Tests;

public class SortingValidatorTests
{
    [SortableFields("UserName", "Email", "CreationTime", DefaultSort = "UserName")]
    private sealed class SortableInput
    {
    }

    [SortableFields("Name")]
    private sealed class NoDefaultSortInput
    {
    }

    [SortableFields("UserName", "Email", "CreationTime", MaxClauses = 2)]
    private sealed class MultiClauseInput
    {
    }

    private sealed class UnsortableInput
    {
    }

    [SortableFields("Name")]
    private class SortableBaseInput
    {
    }

    private sealed class DerivedInput : SortableBaseInput
    {
    }

    [Fact]
    public void Applies_the_declared_default_when_the_caller_sends_nothing()
    {
        Assert.Equal("UserName", SortingValidator.ValidateAndNormalize<SortableInput>(null));
        Assert.Equal("UserName", SortingValidator.ValidateAndNormalize<SortableInput>("   "));
    }

    [Fact]
    public void Returns_null_when_no_default_is_declared_and_the_caller_sends_nothing()
    {
        Assert.Null(SortingValidator.ValidateAndNormalize<NoDefaultSortInput>(null));
    }

    [Fact]
    public void Accepts_a_declared_field_and_canonicalizes_its_casing()
    {
        Assert.Equal("Email", SortingValidator.ValidateAndNormalize<SortableInput>("email"));
        Assert.Equal("Email desc", SortingValidator.ValidateAndNormalize<SortableInput>("EMAIL DESC"));
    }

    [Fact]
    public void Preserves_an_explicit_ascending_direction_rather_than_collapsing_it()
    {
        // Some existing contracts treat a bare field name as descending (Identity's `created` and
        // `lastlogin` order newest-first), so dropping an explicit "asc" would invert the caller's sort.
        Assert.Equal("Email asc", SortingValidator.ValidateAndNormalize<SortableInput>("email asc"));
        Assert.Equal("Email", SortingValidator.ValidateAndNormalize<SortableInput>("email"));
    }

    [Fact]
    public void Accepts_multiple_clauses_and_preserves_their_order_when_the_input_permits_them()
    {
        Assert.Equal(
            "CreationTime desc, UserName asc",
            SortingValidator.ValidateAndNormalize<MultiClauseInput>("creationTime desc, userName asc"));
    }

    [Fact]
    public void Rejects_more_clauses_than_the_input_permits()
    {
        // Single-clause is the default because an application service that maps sorts with a `switch`
        // over the whole string cannot honour a second clause; accepting one would silently downgrade
        // the request to the default order.
        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<SortableInput>("Email desc, UserName"));

        Assert.Contains("one sort clause", exception.Message, StringComparison.Ordinal);

        Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<MultiClauseInput>(
                "Email, UserName, CreationTime"));
    }

    [Fact]
    public void Rejects_a_clause_limit_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SortableFieldsAttribute("Name") { MaxClauses = 0 });
    }

    [Fact]
    public void Rejects_a_field_that_is_not_declared()
    {
        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<SortableInput>("PasswordHash"));

        Assert.Equal(SharedErrorCodes.ValidationFailed, exception.Code);
        Assert.Contains("PasswordHash", exception.Message, StringComparison.Ordinal);
        // The allowed set is disclosed because it is already public API surface, and it turns a dead
        // end into an actionable message.
        Assert.Contains("UserName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_unknown_direction()
    {
        Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<SortableInput>("Email sideways"));
    }

    [Fact]
    public void Rejects_a_malformed_clause()
    {
        Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<SortableInput>("Email desc extra"));
    }

    [Fact]
    public void Rejects_a_repeated_field_rather_than_collapsing_it()
    {
        // Declared on the multi-clause input so this exercises repetition rather than the clause limit.
        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<MultiClauseInput>("Email, email desc"));

        Assert.Contains("repeated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_any_sort_when_the_input_declares_none()
    {
        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<UnsortableInput>("Name"));

        Assert.Contains("does not support sorting", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allows_an_absent_sort_when_the_input_declares_none()
    {
        Assert.Null(SortingValidator.ValidateAndNormalize<UnsortableInput>(null));
    }

    [Fact]
    public void Surfaces_the_failure_against_the_sorting_parameter_so_a_client_can_attach_it()
    {
        var exception = Assert.Throws<SharedValidationException>(
            () => SortingValidator.ValidateAndNormalize<SortableInput>("Nope"));

        Assert.NotNull(exception.Errors);
        Assert.True(exception.Errors!.ContainsKey("sorting"));
        Assert.NotEmpty(exception.Errors["sorting"]);
    }

    [Fact]
    public void Reads_the_declaration_from_a_base_class()
    {
        Assert.Equal("Name", SortingValidator.ValidateAndNormalize<DerivedInput>("name"));
    }

    [Fact]
    public void Exposes_the_declaration_for_the_openapi_transformer_and_contract_tests()
    {
        var declaration = SortingValidator.GetDeclaration<SortableInput>();

        Assert.NotNull(declaration);
        Assert.Equal(new[] { "UserName", "Email", "CreationTime" }, declaration!.Fields);
        Assert.Equal("UserName", declaration.DefaultSort);
        Assert.Null(SortingValidator.GetDeclaration<UnsortableInput>());
    }

    [Fact]
    public void Rejects_an_attribute_declaring_no_usable_field()
    {
        Assert.Throws<ArgumentException>(() => new SortableFieldsAttribute());
        Assert.Throws<ArgumentException>(() => new SortableFieldsAttribute("   "));
    }

    [Fact]
    public void Rejects_an_attribute_declaring_a_duplicate_field()
    {
        Assert.Throws<ArgumentException>(() => new SortableFieldsAttribute("Name", "name"));
    }
}
