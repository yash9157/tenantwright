using Identity.Domain.Shared.Account;

namespace Identity.Application.Account;

public static class InteractiveChallengeStepGuard
{
    public static bool MatchesExpectedStep(
        InteractiveLoginChallengeCacheItem? challenge,
        InteractiveLoginStep expectedStep)
    {
        return challenge != null && challenge.ExpectedStep == expectedStep;
    }
}
