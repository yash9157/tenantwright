namespace Shared.AuditLogs.Auditing;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class IntegrationServiceAttribute : Attribute
{
    public static bool IsDefinedOrInherited(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.IsDefined(typeof(IntegrationServiceAttribute), inherit: true))
        {
            return true;
        }

        foreach (var @interface in type.GetInterfaces())
        {
            if (@interface.IsDefined(typeof(IntegrationServiceAttribute), inherit: true))
            {
                return true;
            }
        }

        return false;
    }
}
