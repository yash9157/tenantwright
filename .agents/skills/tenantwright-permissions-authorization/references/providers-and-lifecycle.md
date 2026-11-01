# Providers and Lifecycle

## Provider Identity

Resolve authority through canonical immutable identifiers:

- user grants use the immutable user ID;
- role grants use the immutable role ID, never a role name;
- client and internal principals use registered identifiers and explicit policies;
- tenant and host assignments require their declared scope.

Normalize once at the boundary and reject malformed, unknown, or cross-tenant identifiers.

## Mutation Path

Grant, deny, wildcard, expiry, feature value, and role-assignment mutations must preserve:

- actor, reason, ticket, and history;
- tenant or host ownership;
- idempotency and optimistic concurrency;
- conflict and approval rules;
- version/stamp advancement and event publication;
- active-role and active-definition checks.

Four-eyes operations cannot be requested and approved by the same actor when the definition requires separation.

## Delegation

Delegation exists in current source through `PermissionDelegationDefinition`, `PermissionDelegationDefinitionVersion`, `TenantDelegationAppService`, and tenant/host delegation controllers. It is not an implicit grant. Validate the published definition version, target kind, scope kind, effect, maximum expiry, manager/approver policies, effective dates, and checksum before applying an assignment.

## Role Lifecycle

Role retirement must prevent new use, reconcile or retire assignments through the owning workflow, advance authorization state, and remain auditable. Never migrate grants by role display name.

## Tests

Cover wrong provider type, mutable-name substitution, inactive role, wrong tenant, duplicate request, stale concurrency token, self-approval, expired assignment, unpublished delegation version, disallowed scope/effect, and event/version propagation.
