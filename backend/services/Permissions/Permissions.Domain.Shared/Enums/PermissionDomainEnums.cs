namespace Permissions.Domain.Shared.Enums;

public enum PermissionGrantEffect : byte
{
    Allow = 1,
    Deny = 2
}

public enum PermissionFeatureRequirement : byte
{
    Enabled = 1,
    EnabledOrTrial = 2
}
