# Policy and Caller Pairing

## Backend Enforcement

Authorize at the HTTP boundary with the exact registered policy and repeat contextual checks in the application layer when access depends on a resource, tenant, or state not represented by the route policy. Controllers remain thin; application services own business authorization.

Host-global operations require explicit host policies, no ambient tenant, intentional filter handling, audit records, and negative tenant tests. Internal APIs require registered client/scope policies; do not let the dynamic permission provider reinterpret those policy names.

Manual checks must use the established permission checker and current tenant/current-user abstractions. Never parse untrusted claims or cached JSON as proof of access.

## Future UI

A future frontend (React and Angular are planned) must gate routes and actions on the exact backend policy. UI gates are never a security boundary; detailed client rules arrive with the frontend.

## Verification

For every caller, prove anonymous, unauthenticated, authenticated-but-denied, wrong-tenant, wrong-host, malformed-context, and allowed behavior. When internal calls are involved, also test wrong client, audience, scope, and unavailable token acquisition.
