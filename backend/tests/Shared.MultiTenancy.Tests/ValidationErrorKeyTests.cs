using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Shared.AspNetCore.ProblemDetails;
using Xunit;

namespace Shared.MultiTenancy.Tests;

/// <summary>
/// The validation <c>errors</c> keys are a contract: a client matching them against its own field names must
/// not have to know which of the three producing paths a given key came from.
/// </summary>
public sealed class ValidationErrorKeyTests
{
    [Theory]
    // DataAnnotations through ModelState: PascalCase CLR property paths.
    [InlineData("Reason", "reason")]
    [InlineData("IdempotencyKey", "idempotencyKey")]
    [InlineData("Contact.Email", "contact.email")]
    [InlineData("Lines[0].NetAmountMinor", "lines[0].netAmountMinor")]
    [InlineData("Components[12].Rate", "components[12].rate")]
    // System.Text.Json body-deserialization failures: JSON paths.
    [InlineData("$.reason", "reason")]
    [InlineData("$.contact.email", "contact.email")]
    [InlineData("$.lines[0].netAmountMinor", "lines[0].netAmountMinor")]
    // Hand-thrown SharedValidationException keys are already the house convention and must survive intact.
    [InlineData("sorting", "sorting")]
    [InlineData("startDate", "startDate")]
    public void Normalize_produces_the_serialized_property_path(string key, string expected)
    {
        Assert.Equal(expected, ValidationErrorKey.Normalize(key));
    }

    [Fact]
    public void Normalize_lowers_every_segment_not_just_the_first()
    {
        // `contact.Email` would be worse than leaving it alone: it looks normalized and still does not match.
        Assert.Equal("outer.inner.leafValue", ValidationErrorKey.Normalize("Outer.Inner.LeafValue"));
    }

    [Fact]
    public void Normalize_keeps_an_acronym_tail_intact()
    {
        // Only the first character of a segment is touched, matching how the JSON serializer camelCases.
        Assert.Equal("tenantID", ValidationErrorKey.Normalize("TenantID"));
    }

    [Fact]
    public void Normalize_leaves_a_whole_body_failure_unattached_to_any_field()
    {
        // `$` alone names no member, so it must not be turned into something that looks like a field name.
        Assert.Equal("$", ValidationErrorKey.Normalize("$"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_returns_empty_for_a_key_that_names_nothing(string? key)
    {
        Assert.Equal(string.Empty, ValidationErrorKey.Normalize(key));
    }
}

/// <summary>
/// The automatic model-validation 400 — the path every <c>[ApiController]</c> DataAnnotations failure takes
/// through <c>InvalidModelStateResponseFactory</c>. It had no test at all, so the PascalCase keys it used to
/// emit were asserted nowhere and nothing would have caught them changing.
/// </summary>
public sealed class SharedProblemDetailsFactoryValidationTests
{
    private static ModelStateDictionary ModelStateWith(params (string Key, string Message)[] errors)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (key, message) in errors)
        {
            modelState.AddModelError(key, message);
        }

        return modelState;
    }

    [Fact]
    public void Model_state_keys_are_normalized_to_the_serialized_property_path()
    {
        var problemDetails = SharedProblemDetailsFactory.CreateValidationProblemDetails(
            new DefaultHttpContext(),
            ModelStateWith(
                ("Reason", "A reason is required."),
                ("Contact.Email", "A valid email address is required."),
                ("Lines[0].NetAmountMinor", "Must be positive.")));

        Assert.Equal(
            new[] { "contact.email", "lines[0].netAmountMinor", "reason" },
            problemDetails.Errors.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_message_for_a_field_is_carried_not_just_the_first()
    {
        var problemDetails = SharedProblemDetailsFactory.CreateValidationProblemDetails(
            new DefaultHttpContext(),
            ModelStateWith(
                ("Reason", "A reason is required."),
                ("Reason", "Use at most 100 characters.")));

        Assert.Equal(
            new[] { "A reason is required.", "Use at most 100 characters." },
            problemDetails.Errors["reason"]);
    }

    [Fact]
    public void A_binding_failure_with_no_message_reports_a_safe_sentence_rather_than_an_empty_string()
    {
        // A type-mismatch entry carries its reason on an exception rather than on ErrorMessage, leaving the
        // message empty. The exception text is not safe to surface and an empty string tells nobody anything.
        var problemDetails = SharedProblemDetailsFactory.CreateValidationProblemDetails(
            new DefaultHttpContext(),
            ModelStateWith(("BirthYear", string.Empty)));

        Assert.Equal(new[] { "The supplied value is not valid." }, problemDetails.Errors["birthYear"]);
    }

    [Fact]
    public void Valid_entries_contribute_no_key()
    {
        var modelState = new ModelStateDictionary();
        modelState.SetModelValue("Reason", "ok", "ok");

        var problemDetails = SharedProblemDetailsFactory.CreateValidationProblemDetails(
            new DefaultHttpContext(),
            modelState);

        Assert.Empty(problemDetails.Errors);
    }

    [Fact]
    public void The_code_and_trace_extensions_still_accompany_a_validation_response()
    {
        var problemDetails = SharedProblemDetailsFactory.CreateValidationProblemDetails(
            new DefaultHttpContext { TraceIdentifier = "trace-1" },
            ModelStateWith(("Reason", "A reason is required.")));

        Assert.Equal(SharedErrorCodes.ValidationFailed, problemDetails.Extensions["code"]);
        Assert.Equal("urn:tenantwright:problem:validation_failed", problemDetails.Type);
        Assert.NotNull(problemDetails.Extensions["traceId"]);
    }
}
