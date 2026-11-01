---
name: tenantwright-permissions-authorization
description: Backend permission definitions, grants, evaluation, features, policies, and tenant-safe authorization.
---
# TenantWright Permissions and Authorization

Treat authorization as one fail-closed path: definition, provider identity, durable grant state, effective evaluation, cache invalidation, and backend policy enforcement.

## Read First

Always read `references/source-map.md`. Then select the relevant detail:

- `references/definitions-and-catalog.md` for definitions, metadata, features, and catalog sync.
- `references/providers-and-lifecycle.md` for provider identities, assignments, approvals, delegation, and role lifecycle.
- `references/evaluation-and-cache.md` for effective decisions, versions, events, and caches.
- `references/policy-and-ui-pairing.md` for policy ownership, host/tenant scope, internal calls, and future UI pairing.
- `references/verification.md` for focused evidence.

## Workflow

1. Locate the exact permission or feature in current source; never invent a key from a route or display label.
2. Identify the owner, caller, immutable subject/provider key, tenant or host scope, and backend policy.
3. Write the expected allow/deny table, including expiry, wildcard or parent matching, features, conflicts, approvals, and disabled state.
4. Change the owning domain/application path and preserve audit history, idempotency, concurrency, and four-eyes rules where applicable.
5. Trace every retained result and invalidate or advance all affected cache/version state.
6. Test allowed, denied, malformed, expired, stale, wrong-tenant, and wrong-provider paths.
7. Update `docs/security.md` when behavior or ownership changes.

## Invariants

- Unknown definitions, providers, tenants, subjects, cache payloads, or policy configuration never grant access.
- Role grants use immutable role IDs, not role names or labels.
- Host operations require explicit host policy and intentional no-tenant execution; bypassing a tenant filter is not authorization.
- Explicit internal, client, or scope policies must not be reinterpreted as dynamic permission names.
- Grant and feature mutations must advance every affected version or cache; TTL is not a correctness mechanism.
- Delegation is allowed only by the currently published definition version and its target, scope, effect, expiry, manager, and approver constraints.
- UI visibility is never a security boundary.

## Completion Evidence

Report the exact definition, provider identity, scope, decision table, mutation and approval behavior, invalidation chain, backend enforcement, focused tests, documentation status, and any runtime check not run.
