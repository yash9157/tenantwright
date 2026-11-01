using System.Reflection;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Auditing;

public enum AuditDecision
{
    Audited = 0,
    SkippedByPolicy = 1,
    SkippedByConfig = 2
}

public sealed class AuditPolicyResult
{
    public bool ShouldAudit { get; }

    public AuditDecision Decision { get; }

    public string? Reason { get; }

    public bool IsObserveOnly { get; }

    private AuditPolicyResult(bool shouldAudit, AuditDecision decision, string? reason, bool isObserveOnly = false)
    {
        ShouldAudit = shouldAudit;
        Decision = decision;
        Reason = reason;
        IsObserveOnly = isObserveOnly;
    }

    public static AuditPolicyResult Audited()
    {
        return new AuditPolicyResult(true, AuditDecision.Audited, reason: null);
    }

    public static AuditPolicyResult Skipped(AuditDecision decision, string reason)
    {
        return new AuditPolicyResult(false, decision, reason);
    }

    public static AuditPolicyResult ObserveOnlyAudited(string reason)
    {
        return new AuditPolicyResult(true, AuditDecision.Audited, reason, isObserveOnly: true);
    }
}

public interface IAuditPolicyEvaluator
{
    AuditPolicyResult EvaluateControllerPolicy(
        Type controllerType,
        MethodInfo methodInfo,
        bool shouldAuditByRuntimeRules,
        SharedAuditingOptions options);

    AuditPolicyResult EvaluateServicePolicy(
        Type? serviceType,
        MethodInfo methodInfo,
        bool shouldAuditByRuntimeRules,
        SharedAuditingOptions options);
}

public sealed class AuditPolicyEvaluator : IAuditPolicyEvaluator, ITransientDependency
{
    public AuditPolicyResult EvaluateControllerPolicy(
        Type controllerType,
        MethodInfo methodInfo,
        bool shouldAuditByRuntimeRules,
        SharedAuditingOptions options)
    {
        if (!shouldAuditByRuntimeRules)
        {
            return AuditPolicyResult.Skipped(AuditDecision.SkippedByConfig, "config_rule");
        }

        if (!options.RequireExplicitControllerAuditing)
        {
            return AuditPolicyResult.Audited();
        }

        if (HasDisableAuditing(methodInfo, controllerType))
        {
            return AuditPolicyResult.Skipped(AuditDecision.SkippedByPolicy, "policy_disabled");
        }

        if (HasAudited(methodInfo, controllerType))
        {
            return AuditPolicyResult.Audited();
        }

        return options.EnforcementMode == AuditEnforcementMode.Strict
            ? AuditPolicyResult.Skipped(AuditDecision.SkippedByPolicy, "policy_explicit_required")
            : AuditPolicyResult.ObserveOnlyAudited("policy_explicit_required_observe");
    }

    public AuditPolicyResult EvaluateServicePolicy(
        Type? serviceType,
        MethodInfo methodInfo,
        bool shouldAuditByRuntimeRules,
        SharedAuditingOptions options)
    {
        if (!shouldAuditByRuntimeRules)
        {
            return AuditPolicyResult.Skipped(AuditDecision.SkippedByConfig, "config_rule");
        }

        if (!options.RequireExplicitServiceAuditing)
        {
            return AuditPolicyResult.Audited();
        }

        if (HasDisableAuditing(methodInfo, serviceType))
        {
            return AuditPolicyResult.Skipped(AuditDecision.SkippedByPolicy, "policy_disabled");
        }

        if (HasAudited(methodInfo, serviceType))
        {
            return AuditPolicyResult.Audited();
        }

        return options.EnforcementMode == AuditEnforcementMode.Strict
            ? AuditPolicyResult.Skipped(AuditDecision.SkippedByPolicy, "policy_explicit_required")
            : AuditPolicyResult.ObserveOnlyAudited("policy_explicit_required_observe");
    }

    private static bool HasAudited(MethodInfo methodInfo, Type? declaringType)
    {
        return methodInfo.IsDefined(typeof(AuditedAttribute), inherit: true) ||
               (declaringType != null && declaringType.IsDefined(typeof(AuditedAttribute), inherit: true));
    }

    private static bool HasDisableAuditing(MethodInfo methodInfo, Type? declaringType)
    {
        return methodInfo.IsDefined(typeof(DisableAuditingAttribute), inherit: true) ||
               (declaringType != null && declaringType.IsDefined(typeof(DisableAuditingAttribute), inherit: true));
    }
}
