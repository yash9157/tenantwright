# Auth Security Rules

## Session and Revocation

- Enforce `session_id` on session-bound user tokens.
- Missing, unknown, expired, or revoked sessions fail with `401`.
- Keep cache markers and persisted session records consistent where both participate in validation.
- Revocation must take effect through the established version/cache path; do not add a permissive grace period.
- `SessionValidationMiddleware` is the shared request-path owner; verify registration and exclusions in each affected host.

## Token and Claim Safety

- Validate issuer, signature, expiry, audience, client, scopes, presenter, tenant, and token type through the established OpenIddict pipeline.
- Set explicit destinations; do not place internal claims into identity or access tokens accidentally.
- Reconstruct authorization consistently during refresh. Never silently truncate roles or permissions.
- Keep lifetimes bounded and reject ambiguous tenant or subject evidence.
- Never log access tokens, refresh tokens, passwords, secrets, full connection strings, or sensitive identity payloads.

## Interactive Flows

- Preserve anti-enumeration for login, reset, verification, and invitation flows.
- Preserve rate limiting, request-forgery protection, secure cookie settings, and local return-URL validation.
- Do not weaken runtime checks to simplify tests.

## Configuration

Invalid issuer, signing/encryption key, client, redirect URI, permission-bootstrap, or internal-token configuration must fail startup or block the affected flow. `GlobalExceptionHandlingMiddleware` must not expose sensitive details.

## Required Negative Tests

Cover missing session, revoked session, tenant mismatch, wrong client/audience/scope, expired token, unsafe return URL, enumeration-resistant responses, invalid startup configuration, and concurrent refresh/revoke behavior as applicable.

Keep `docs/security.md` aligned with material auth or identity behavior.
