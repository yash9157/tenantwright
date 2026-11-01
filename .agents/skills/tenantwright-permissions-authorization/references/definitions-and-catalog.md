# Definitions and Catalog

## Definition Ownership

Service permission constants and definition providers live in each service's `Domain.Shared` project. `Permissions.Domain.Shared` owns management permissions; `Shared.Permissions.Abstractions` owns common definition metadata and contracts.

A change is complete only when the constant, definition provider, catalog synchronization, policy consumer, seed behavior, and tests agree.

## Rules

- Use the exact source constant. Existing examples include `AuditLogs.Read`, `Notifications.Inbox.Read`, `Permissions.Management.Grants`, `Identity.Users.ManagePermissions`, and `Tenant.Management.ConnectionStrings.Read`.
- Keep names stable, ordinal, and service-owned. Display text is not an authority key.
- Declare parent/child relationships, enabled state, side, feature requirements, and delegation metadata explicitly.
- Reject duplicate names and conflicting metadata during catalog synchronization.
- Do not make catalog/bootstrap endpoints depend on the remote permission data they load.
- A feature requirement narrows a permission; it never grants the permission by itself.

## Delegation Metadata

`Shared.Permissions.Abstractions` exposes delegable-permission metadata. The Permissions service synchronizes it into `PermissionDelegationDefinition` and immutable version records. Before relying on delegation, verify the published version's allowed target kinds, scope kinds, effects, expiry/review limits, manager and approver permissions, checksum, and effective period.

## Verification

Test duplicate and malformed definitions, parent relationships, feature requirements, sync idempotency, retired definitions, and published delegation-version selection. Update `docs/security.md` when the public authorization model changes.
