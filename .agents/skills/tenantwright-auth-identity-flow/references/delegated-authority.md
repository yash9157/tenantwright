# Delegated Authority and Token Exchange

Keep interactive user issuance, refresh reconstruction, client credentials, and delegated exchange distinct. A service-to-service lookup uses the established internal token provider. A user-delegated action uses its explicitly authorized exchange path; forwarding an incoming bearer token or inventing claims is not a substitute.

## Discover the Path

From `backend/`:

```powershell
rg -n "TokenExchange|subject_token|actor_token|requested_token_type|SetAudiences|SetDestinations" app shared services -g "*.cs"
rg -n "Delegation|Presenter|SessionId|GrantRevision|PolicyRevision" app/Auth shared/Shared.Security tests/Auth.Tests -g "*.cs"
rg -n "InternalAccessTokenProvider|RemoteFeatureAccessTokenProvider|client_id" shared services app -g "*.cs"
```

Read the requesting client allow-list, token endpoint policy, shared delegation contracts, caller acquisition code, receiving policy, and negative tests. Protocol support alone does not authorize a client, scope, audience, or exchange.

## Decision Record

Before editing, record the requesting client, authenticated subject and presenter, actor representation, audience/resource, scopes, tenant/session, policy or grant revision, lifetime, and downstream enforcement.

## Rules

- Reject unsupported token types, broader targets, extra scopes, malformed IDs, and conflicting tenant or subject evidence.
- Validate source tokens through OpenIddict; decoding a JWT is not validation.
- Preserve subject and actor accountability without allowing substitution.
- Include only claims intended for the receiving audience and explicit destinations.
- Bound output lifetime by source evidence and owning policy expiry.
- Re-check revocation, session, and grant state where the flow requires live authorization.
- Include every authority dimension in delegated-token cache keys.
- Never fall back to a broader service token when delegated exchange fails.
- Carry cancellation and fail closed on unavailable dependencies.

## Proof

Test wrong client, presenter, audience, scope, token type, subject, session, tenant, expiry, stale revision, replay, and denied exchange as applicable. Test both issuance and receiving enforcement; helper-only tests do not prove token validation or policy composition.

Permission evaluation remains with `tenantwright-permissions-authorization`; HTTP/token caching infrastructure belongs to `tenantwright-service-runtime`. Update `docs/security.md` and the approved runtime smoke evidence without exposing token values.

Protocol reference: [RFC 8693](https://www.rfc-editor.org/rfc/rfc8693.html).
