using Permissions.Application.Caching;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionStampMonotonicityTests
{
    [Fact]
    public void ComputeNextStamp_should_advance_past_current_stamp_when_current_is_in_the_future_or_same_tick()
    {
        // Simulates two bumps happening within the same UTC tick: the second bump's "current" stamp
        // (the first bump's result) is >= DateTime.UtcNow.Ticks by the time it re-reads. The fix must
        // still produce a strictly greater value instead of colliding on the same tick.
        var currentStamp = DateTime.UtcNow.Ticks + 1_000_000; // pretend "now" already advanced past this tick window

        var next = PermissionStampService.ComputeNextStamp(currentStamp);

        Assert.True(next > currentStamp, "Next stamp must be strictly greater than the current stamp.");
    }

    [Fact]
    public void ComputeNextStamp_should_use_utc_now_when_it_has_already_advanced_past_current_stamp()
    {
        var staleStamp = DateTime.UtcNow.AddDays(-1).Ticks;

        var next = PermissionStampService.ComputeNextStamp(staleStamp);

        Assert.True(next > staleStamp);
        Assert.True(next >= DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(5).Ticks, "Should track close to UtcNow when current stamp is stale.");
    }

    [Fact]
    public void Two_sequential_bumps_in_the_same_tick_window_should_never_produce_equal_stamps()
    {
        // Regression for the bug: DateTime.UtcNow.Ticks default could collide across two bumps that
        // both read UtcNow within the same tick resolution window (rare, but real on fast hardware).
        var firstBumpStamp = PermissionStampService.ComputeNextStamp(0);
        var secondBumpStamp = PermissionStampService.ComputeNextStamp(firstBumpStamp);

        Assert.True(secondBumpStamp > firstBumpStamp);
        Assert.NotEqual(firstBumpStamp, secondBumpStamp);
    }

    [Fact]
    public void ComputeNextStamp_should_be_strictly_increasing_across_many_rapid_calls()
    {
        var stamp = 0L;
        for (var i = 0; i < 1000; i++)
        {
            var next = PermissionStampService.ComputeNextStamp(stamp);
            Assert.True(next > stamp, $"Iteration {i}: expected {next} > {stamp}");
            stamp = next;
        }
    }
}
