using Shared.Guids;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public sealed class UuidV7GuidGeneratorTests
{
    [Fact]
    public void Create_returns_rfc_uuid_version_7()
    {
        var id = UuidV7GuidGenerator.Instance.Create();

        Assert.Equal(7, id.Version);
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void Create_returns_unique_values()
    {
        var ids = Enumerable.Range(0, 1_024)
            .Select(_ => UuidV7GuidGenerator.Instance.Create())
            .ToHashSet();

        Assert.Equal(1_024, ids.Count);
        Assert.All(ids, id => Assert.Equal(7, id.Version));
    }
}
