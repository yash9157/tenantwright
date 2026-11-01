namespace Permissions.Domain.Shared.Constants;

public static class PermissionDefinitionRecordConsts
{
    public static int MaxNameLength { get; set; } = 128;
    public static int MaxDisplayNameLength { get; set; } = 256;
    public static int MaxProvidersLength { get; set; } = 128;
    public static int MaxStateCheckersLength { get; set; } = 256;
    public static int MaxResourceNameLength { get; set; } = 256;
    public static int MaxManagementPermissionNameLength { get; set; } = 128;
}

public static class PermissionGrantConsts
{
    public static int MaxProviderNameLength { get; set; } = 64;
    public static int MaxProviderKeyLength { get; set; } = 64;
    public static int MaxResourceNameLength { get; set; } = 256;
    public static int MaxResourceKeyLength { get; set; } = 256;
    public static int MaxReasonLength { get; set; } = 512;
    public static int MaxTicketIdLength { get; set; } = 128;
}

public static class PermissionGroupDefinitionRecordConsts
{
    public static int MaxNameLength { get; set; } = 128;
    public static int MaxDisplayNameLength { get; set; } = 256;
}
