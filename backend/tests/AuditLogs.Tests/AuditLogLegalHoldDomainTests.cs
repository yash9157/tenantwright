using AuditLogs.Domain.Auditing;
using Xunit;

namespace AuditLogs.Tests;

/// <summary>
/// Sub-flow 5: legal hold scoping and coverage rules.
/// </summary>
public class AuditLogLegalHoldDomainTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Constructor_should_reject_a_completely_unscoped_hold()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AuditLogLegalHold(
            id: 0,
            name: "unscoped",
            tenantId: null,
            fromUtc: null,
            toUtc: null,
            userId: null));

        Assert.Contains("at least one", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_should_reject_toUtc_before_fromUtc()
    {
        var from = DateTime.SpecifyKind(new DateTime(2026, 1, 10), DateTimeKind.Utc);
        var to = from.AddDays(-1);

        Assert.Throws<ArgumentException>(() => new AuditLogLegalHold(
            id: 0,
            name: "bad-range",
            tenantId: TenantA,
            fromUtc: from,
            toUtc: to,
            userId: null));
    }

    [Fact]
    public void Covers_should_treat_unset_dimensions_as_wildcards()
    {
        var hold = new AuditLogLegalHold(
            id: 1,
            name: "tenant-wide-hold",
            tenantId: TenantA,
            fromUtc: null,
            toUtc: null,
            userId: null);

        Assert.True(hold.Covers(TenantA, DateTime.UtcNow, "any-user"));
        Assert.True(hold.Covers(TenantA, DateTime.UtcNow.AddYears(-5), "other-user"));
        Assert.False(hold.Covers(TenantB, DateTime.UtcNow, "any-user"));
    }

    [Fact]
    public void Covers_should_respect_date_range_dimension()
    {
        var from = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(new DateTime(2026, 1, 31), DateTimeKind.Utc);
        var hold = new AuditLogLegalHold(
            id: 1,
            name: "january-hold",
            tenantId: null,
            fromUtc: from,
            toUtc: to,
            userId: null);

        Assert.True(hold.Covers(null, from.AddDays(15), null));
        Assert.False(hold.Covers(null, from.AddDays(-1), null));
        Assert.False(hold.Covers(null, to.AddDays(1), null));
    }

    [Fact]
    public void Covers_should_respect_user_dimension()
    {
        var hold = new AuditLogLegalHold(
            id: 1,
            name: "user-hold",
            tenantId: null,
            fromUtc: null,
            toUtc: null,
            userId: "user-42");

        Assert.True(hold.Covers(null, DateTime.UtcNow, "user-42"));
        Assert.False(hold.Covers(null, DateTime.UtcNow, "user-43"));
        Assert.False(hold.Covers(null, DateTime.UtcNow, null));
    }

    [Fact]
    public void Covers_should_require_all_set_dimensions_to_match()
    {
        var from = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(new DateTime(2026, 1, 31), DateTimeKind.Utc);
        var hold = new AuditLogLegalHold(
            id: 1,
            name: "combined-hold",
            tenantId: TenantA,
            fromUtc: from,
            toUtc: to,
            userId: "user-42");

        Assert.True(hold.Covers(TenantA, from.AddDays(5), "user-42"));
        Assert.False(hold.Covers(TenantB, from.AddDays(5), "user-42"));
        Assert.False(hold.Covers(TenantA, from.AddDays(5), "someone-else"));
        Assert.False(hold.Covers(TenantA, to.AddDays(1), "user-42"));
    }

    [Fact]
    public void Covers_should_return_false_once_released()
    {
        var hold = new AuditLogLegalHold(
            id: 1,
            name: "released-hold",
            tenantId: TenantA,
            fromUtc: null,
            toUtc: null,
            userId: null);

        Assert.True(hold.Covers(TenantA, DateTime.UtcNow, null));

        hold.Release("investigation closed");

        Assert.False(hold.Covers(TenantA, DateTime.UtcNow, null));
        Assert.True(hold.IsReleased);
        Assert.NotNull(hold.ReleasedAtUtc);
        Assert.Equal("investigation closed", hold.Reason);
    }

    [Fact]
    public void Release_should_be_idempotent()
    {
        var hold = new AuditLogLegalHold(
            id: 1,
            name: "hold",
            tenantId: TenantA,
            fromUtc: null,
            toUtc: null,
            userId: null);

        hold.Release("first reason");
        var firstReleasedAt = hold.ReleasedAtUtc;
        hold.Release("second reason - should be ignored");

        Assert.Equal(firstReleasedAt, hold.ReleasedAtUtc);
        Assert.Equal("first reason", hold.Reason);
    }
}
