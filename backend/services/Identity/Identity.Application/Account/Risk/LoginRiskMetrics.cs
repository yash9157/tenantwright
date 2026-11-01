using System.Diagnostics.Metrics;
using Identity.Domain.Shared.Account;

namespace Identity.Application.Account.Risk;

public static class LoginRiskMetrics
{
    public const string MeterName = "Identity.LoginRisk";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> RiskDecisionsCounter = Meter.CreateCounter<long>("identity_login_risk_decisions_total");
    private static readonly Counter<long> ChallengesCounter = Meter.CreateCounter<long>("identity_login_challenges_total");
    private static readonly Counter<long> BlockedCounter = Meter.CreateCounter<long>("identity_login_blocked_total");
    private static readonly Counter<long> FalsePositiveCounter = Meter.CreateCounter<long>("identity_login_false_positive_total");
    private static readonly Counter<long> LockoutCounter = Meter.CreateCounter<long>("identity_login_lockout_total");
    private static readonly Counter<long> PasswordResetRequestedCounter = Meter.CreateCounter<long>("identity_password_reset_requested_total");
    private static readonly Counter<long> InteractiveChallengeCreatedCounter = Meter.CreateCounter<long>("identity_interactive_challenge_created_total");
    private static readonly Counter<long> InteractiveChallengeConsumedCounter = Meter.CreateCounter<long>("identity_interactive_challenge_consumed_total");
    private static readonly Counter<long> InteractiveChallengeExpiredCounter = Meter.CreateCounter<long>("identity_interactive_challenge_expired_total");
    private static readonly Counter<long> InteractiveChallengeReplayedCounter = Meter.CreateCounter<long>("identity_interactive_challenge_replayed_total");
    private static readonly Histogram<double> RiskScoreHistogram = Meter.CreateHistogram<double>("identity_login_risk_score");

    public static void RecordDecision(LoginRiskLevel level, LoginRiskAction action, int score)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("level", level.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("action", action.ToString().ToLowerInvariant())
        };

        RiskDecisionsCounter.Add(1, tags);
        RiskScoreHistogram.Record(score, tags);

        if (action == LoginRiskAction.ForceMfa)
        {
            ChallengesCounter.Add(1, tags);
        }

        if (action == LoginRiskAction.Block)
        {
            BlockedCounter.Add(1, tags);
        }
    }

    public static void RecordFalsePositive()
    {
        FalsePositiveCounter.Add(1);
    }

    public static void RecordLockout()
    {
        LockoutCounter.Add(1);
    }

    public static void RecordPasswordResetRequested()
    {
        PasswordResetRequestedCounter.Add(1);
    }

    public static void RecordInteractiveChallengeCreated()
    {
        InteractiveChallengeCreatedCounter.Add(1);
    }

    public static void RecordInteractiveChallengeConsumed()
    {
        InteractiveChallengeConsumedCounter.Add(1);
    }

    public static void RecordInteractiveChallengeExpired()
    {
        InteractiveChallengeExpiredCounter.Add(1);
    }

    public static void RecordInteractiveChallengeReplayed()
    {
        InteractiveChallengeReplayedCounter.Add(1);
    }
}
