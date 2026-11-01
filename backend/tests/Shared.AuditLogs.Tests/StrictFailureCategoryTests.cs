using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Xunit;

namespace Shared.AuditLogs.Tests;

/// <summary>
/// Sub-flow 3: per-category Strict enforcement opt-in. A global HideErrors=true (the default) swallows
/// audit write failures so the audited operation isn't blocked by a flaky audit pipeline - except for
/// categories explicitly listed in <see cref="SharedAuditingOptions.StrictFailureCategories"/>, which
/// must fail closed instead.
/// </summary>
public class StrictFailureCategoryTests
{
    [Fact]
    public void IsStrictFailureCategory_should_match_case_insensitively()
    {
        var options = new SharedAuditingOptions();
        options.StrictFailureCategories.Add("Auth");

        Assert.True(options.IsStrictFailureCategory("auth"));
        Assert.True(options.IsStrictFailureCategory("AUTH"));
        Assert.True(options.IsStrictFailureCategory("Auth"));
        Assert.False(options.IsStrictFailureCategory("permissions.grant"));
    }

    [Fact]
    public void IsStrictFailureCategory_should_return_false_for_null_or_empty_category_or_list()
    {
        var options = new SharedAuditingOptions();

        Assert.False(options.IsStrictFailureCategory(null));
        Assert.False(options.IsStrictFailureCategory(string.Empty));
        Assert.False(options.IsStrictFailureCategory("auth"));
    }

    [Fact]
    public void SharedAuditingOptionsPostConfigure_should_normalize_and_deduplicate_categories()
    {
        var options = new SharedAuditingOptions();
        options.StrictFailureCategories.Add("  Auth  ");
        options.StrictFailureCategories.Add("auth");
        options.StrictFailureCategories.Add("");
        options.StrictFailureCategories.Add("permissions.grant");

        SharedAuditingOptionsPostConfigure.Normalize(options);

        Assert.Equal(2, options.StrictFailureCategories.Count);
        Assert.Contains("Auth", options.StrictFailureCategories);
        Assert.Contains("permissions.grant", options.StrictFailureCategories);
    }

    [Fact]
    public async Task EventBusAuditingStore_should_swallow_publish_failures_for_non_strict_categories_when_hide_errors_is_enabled()
    {
        var publisher = new Mock<IAuditLogWriteRequestPublisher>();
        publisher
            .Setup(x => x.PublishAsync(It.IsAny<AuditLogWriteRequest>(), default))
            .ThrowsAsync(new InvalidOperationException("event bus unavailable"));

        var store = new EventBusAuditingStore(
            publisher.Object,
            Options.Create(new SharedAuditingOptions { HideErrors = true }),
            Options.Create(new AuditRedactionOptions()),
            NullLogger<EventBusAuditingStore>.Instance);

        var auditInfo = new AuditLogInfo { Category = "http.request" };

        // Should not throw - HideErrors swallows the failure for a non-strict category.
        await store.SaveAsync(auditInfo);
    }

    [Fact]
    public async Task EventBusAuditingStore_should_propagate_publish_failures_for_strict_categories_even_when_hide_errors_is_enabled()
    {
        var publisher = new Mock<IAuditLogWriteRequestPublisher>();
        publisher
            .Setup(x => x.PublishAsync(It.IsAny<AuditLogWriteRequest>(), default))
            .ThrowsAsync(new InvalidOperationException("event bus unavailable"));

        var options = new SharedAuditingOptions { HideErrors = true };
        options.StrictFailureCategories.Add("auth");

        var store = new EventBusAuditingStore(
            publisher.Object,
            Options.Create(options),
            Options.Create(new AuditRedactionOptions()),
            NullLogger<EventBusAuditingStore>.Instance);

        var auditInfo = new AuditLogInfo { Category = "auth" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(auditInfo));
    }

    [Fact]
    public async Task EventBusAuditingStore_should_propagate_failures_for_any_category_when_hide_errors_is_disabled()
    {
        var publisher = new Mock<IAuditLogWriteRequestPublisher>();
        publisher
            .Setup(x => x.PublishAsync(It.IsAny<AuditLogWriteRequest>(), default))
            .ThrowsAsync(new InvalidOperationException("event bus unavailable"));

        var store = new EventBusAuditingStore(
            publisher.Object,
            Options.Create(new SharedAuditingOptions { HideErrors = false }),
            Options.Create(new AuditRedactionOptions()),
            NullLogger<EventBusAuditingStore>.Instance);

        var auditInfo = new AuditLogInfo { Category = "http.request" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(auditInfo));
    }

    [Fact]
    public void AuditedAttribute_should_expose_an_optional_category_override()
    {
        var method = typeof(SampleController).GetMethod(nameof(SampleController.Login))!;
        var attribute = (AuditedAttribute)Attribute.GetCustomAttribute(method, typeof(AuditedAttribute))!;

        Assert.Equal("auth", attribute.Category);
    }

    private sealed class SampleController
    {
        [Audited(Category = "auth")]
        public void Login()
        {
        }
    }
}
