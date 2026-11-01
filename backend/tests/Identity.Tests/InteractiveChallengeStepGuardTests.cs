using Identity.Application.Account;
using Identity.Domain.Shared.Account;
using Xunit;

namespace Identity.Tests;

public class InteractiveChallengeStepGuardTests
{
    [Fact]
    public void MatchesExpectedStep_Returns_False_When_Challenge_Is_Null()
    {
        var result = InteractiveChallengeStepGuard.MatchesExpectedStep(null, InteractiveLoginStep.RequiresMfa);

        Assert.False(result);
    }

    [Fact]
    public void MatchesExpectedStep_Returns_False_When_Step_Mismatches()
    {
        var challenge = new InteractiveLoginChallengeCacheItem
        {
            ExpectedStep = InteractiveLoginStep.RequiresMfa
        };

        var result = InteractiveChallengeStepGuard.MatchesExpectedStep(
            challenge,
            InteractiveLoginStep.RequiresTrustDevice);

        Assert.False(result);
    }

    [Fact]
    public void MatchesExpectedStep_Returns_True_When_Step_Matches()
    {
        var challenge = new InteractiveLoginChallengeCacheItem
        {
            ExpectedStep = InteractiveLoginStep.PasswordExpired
        };

        var result = InteractiveChallengeStepGuard.MatchesExpectedStep(
            challenge,
            InteractiveLoginStep.PasswordExpired);

        Assert.True(result);
    }
}
