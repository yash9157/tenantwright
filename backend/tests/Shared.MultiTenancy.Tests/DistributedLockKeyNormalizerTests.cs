using Microsoft.Extensions.Options;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Implementations;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public sealed class DistributedLockKeyNormalizerTests
{
    [Fact]
    public void IgnoreTenant_scope_should_not_collide_with_host_scope()
    {
        var normalizer = new DefaultDistributedLockKeyNormalizer(
            new TestCurrentTenant(),
            new TestOptionsMonitor<DistributedLockingOptions>(new DistributedLockingOptions
            {
                KeyPrefix = "tenantwright:"
            }));

        var hostKey = normalizer.NormalizeKey(new DistributedLockKeyNormalizeArgs("jobs"));
        var globalKey = normalizer.NormalizeKey(new DistributedLockKeyNormalizeArgs("jobs", ignoreTenant: true));

        Assert.NotEqual(hostKey, globalKey);
        Assert.Equal("t:host,l:tenantwright:jobs", hostKey);
        Assert.Equal("t:global,l:tenantwright:jobs", globalKey);
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => false;
        public Guid? Id => null;
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => new DisposeAction();
    }

    private sealed class DisposeAction : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
