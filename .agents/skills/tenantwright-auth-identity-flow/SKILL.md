---
name: tenantwright-auth-identity-flow
description: Backend authentication, OIDC and OAuth tokens, identity lifecycle, MFA, sessions, and internal identity policies.
---
# TenantWright Auth and Identity Flow

Keep authentication, token issuance, identity lifecycle, and session behavior fail closed across initial issuance, refresh, revocation, and downstream validation.

## Read First

- Read `references/auth-source-map.md` for current owners and discovery.
- Read `references/auth-security-rules.md` for every security-sensitive change.
- Read `references/delegated-authority.md` for token exchange, delegated authority, or internal calls carrying user context.

## Workflow

1. Classify ownership between the Auth host, Identity service, shared security infrastructure, and permission evaluation.
2. Define subject, tenant, client, scopes, audiences, assurance, session, token destinations, lifetime, and failure semantics.
3. Trace every issuance and reconstruction path before changing claims or identity contracts.
4. Preserve anti-enumeration, rate limits, request-forgery defenses, secure cookies, safe return URLs, and revoke/refresh invariants.
5. Use immutable IDs for security state. Names are presentation unless a protocol explicitly requires them.
6. Route grant/effective-access work to `tenantwright-permissions-authorization` and API/event shape work to `tenantwright-api-event-contracts`.
7. Update focused tests and `docs/security.md` when behavior changes.

## Verification

Run the affected Auth, Identity, Notifications, Permissions, or shared-security test project. The checked-in auth token/permission smoke script requires running services and explicit user approval.

## Definition of Done

- Issuance, refresh, revoke, session validation, and downstream enforcement agree.
- Invalid, expired, wrong-client, wrong-audience, wrong-tenant, revoked, and concurrent paths fail closed.
- Claims and secrets are minimized, destination-bound, bounded, and never logged.
- Tests and flat canonical documentation match current behavior.
