namespace Permissions.Domain.Shared.Enums;

public enum PermissionDelegationDefinitionLifecycle : short { Active = 1, Retired = 2 }
public enum PermissionDelegationDefinitionPublicationState : short { Draft = 1, Published = 2, Retired = 3 }

[Flags]
public enum PermissionDelegationTargetKinds : short { None = 0, User = 1, Role = 2 }
[Flags]
public enum PermissionDelegationScopeKinds : short { None = 0, Tenant = 1, Resource = 4 }
[Flags]
public enum PermissionDelegationEffects : short { None = 0, Allow = 1, Deny = 2 }

public enum PermissionGrantScopeKind : short { Provider = 1, Resource = 2 }
public enum ResourceAccessApprovalState : short { NotRequired = 1, Pending = 2, Approved = 3, Rejected = 4 }
public enum ResourceAccessReviewState : short { NotRequired = 1, Current = 2, Due = 3, Overdue = 4, Certified = 5, Revoked = 6 }
