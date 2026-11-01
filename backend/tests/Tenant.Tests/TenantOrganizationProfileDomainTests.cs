using Tenant.Domain.Tenants;
using Xunit;
using TenantEntity = Tenant.Domain.Tenants.Tenant;

namespace Tenant.Tests;

public sealed class TenantOrganizationProfileDomainTests
{
    [Fact]
    public void Organization_profile_accepts_indian_contact_values()
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "MediCare India");
        tenant.SetOrganizationProfile("Admin", "admin@example.in", "+919876543210", "Line 1", null, "Pune", "Maharashtra", "411001");
        Assert.Equal("411001", tenant.PinCode);
        Assert.Equal("Maharashtra", tenant.StateOrUnionTerritory);
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("12345")]
    [InlineData("ABC123")]
    public void Organization_profile_rejects_invalid_indian_pin(string pin)
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "MediCare India");
        Assert.Throws<ArgumentException>(() => tenant.SetOrganizationProfile(null, null, null, null, null, null, null, pin));
    }

    [Fact]
    public void Organization_profile_rejects_invalid_email()
    {
        var tenant = new TenantEntity(Guid.NewGuid(), "MediCare India");
        Assert.Throws<ArgumentException>(() => tenant.SetOrganizationProfile(null, "not-an-email", null, null, null, null, null, null));
    }
}
