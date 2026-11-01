using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace TenantWright.AppHost.Tests;

public sealed class RabbitMqPasswordHashTests
{
    /// <summary>The worked example from https://www.rabbitmq.com/docs/passwords#computing-password-hash.</summary>
    [Fact]
    public void Create_should_match_the_documented_rabbitmq_example()
    {
        byte[] salt = [0x90, 0x8D, 0xC6, 0x0A];

        Assert.Equal("kI3GCqW5JLMJa4iX1lo7X4D6XbYqlLgxIs30+P6tENUV2POR", RabbitMqPasswordHash.Create("test12", salt));
    }

    [Fact]
    public void Create_should_be_base64_of_a_random_four_byte_salt_and_the_sha256_of_salt_and_password()
    {
        const string password = "local-password-\u00e9";

        var hash = RabbitMqPasswordHash.Create(password);

        var decoded = Convert.FromBase64String(hash);
        Assert.Equal(36, decoded.Length);
        var salt = decoded[..4];
        var expected = SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(password)]);
        Assert.Equal(expected, decoded[4..]);
        Assert.Equal("rabbit_password_hashing_sha256", RabbitMqPasswordHash.HashingAlgorithm);
    }

    [Fact]
    public void Create_should_use_a_fresh_salt_each_time()
    {
        var hashes = Enumerable.Range(0, 8).Select(_ => RabbitMqPasswordHash.Create("same-password")).ToHashSet(StringComparer.Ordinal);

        Assert.True(hashes.Count > 1);
        Assert.All(hashes, hash => Assert.True(RabbitMqPasswordHash.Verify("same-password", hash)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("kI3GCqW5JLMJa4iX1lo7X4D6XbYqlLgxIs30+P6tENUV")]
    [InlineData("kI3GCqW5JLMJa4iX1lo7X4D6XbYqlLgxIs30+P6tENUV2POR2POR")]
    public void Verify_should_reject_malformed_hashes(string? hash)
    {
        Assert.False(RabbitMqPasswordHash.Verify("test12", hash));
    }

    [Fact]
    public void Verify_should_reject_a_different_password()
    {
        Assert.True(RabbitMqPasswordHash.Verify("test12", "kI3GCqW5JLMJa4iX1lo7X4D6XbYqlLgxIs30+P6tENUV2POR"));
        Assert.False(RabbitMqPasswordHash.Verify("test13", "kI3GCqW5JLMJa4iX1lo7X4D6XbYqlLgxIs30+P6tENUV2POR"));
    }

    [Fact]
    public void Create_should_reject_a_salt_of_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => RabbitMqPasswordHash.Create("test12", new byte[8]));
    }
}
