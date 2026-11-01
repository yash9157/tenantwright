# Evaluation and Cache

## Decision Inputs

An effective decision can depend on tenant or host scope, subject, user and role grants, allow/deny effect, exact/parent/wildcard matching, expiry, feature values, conflict rules, role state, and published delegation constraints. Write this table before changing precedence.

Deny, malformed state, unknown definitions, unavailable required dependencies, and ambiguous scope must fail closed.

## Current Boundaries

`Shared.Permissions` provides the dynamic policy/checking path and remote clients. It also contains `InternalAccessTokenProvider` and `RemoteFeatureAccessTokenProvider`. The Permissions service owns durable grants, features, snapshots, access reviews, and mutation workflows.

Search current source for the concrete event consumers rather than assuming class names:

```powershell
rg -n "PermissionGrantChanged|FeatureValuesChanged|Stamp|Snapshot|Cache" services/Permissions shared/Shared.Permissions -g "*.cs"
```

## Cache Correctness

- Include every authority dimension in a cache key: tenant/host scope, subject/provider, permission or feature, and relevant version.
- Advance provider, grant, feature, snapshot, and decision versions affected by a mutation.
- Publish invalidation only after durable state is committed through the established transaction/outbox path.
- Treat missing, corrupt, stale, or unverifiable cache data as a miss or denial, never an allow.
- TTL limits staleness impact but does not replace explicit invalidation.
- Valkey is the runtime cache; package and API names may still contain `Redis`.

## Verification

Test cold and warm decisions, allow/deny precedence, expiry, role changes, feature changes, event replay, duplicate delivery, stale versions, cache outage, corrupt payload, wrong tenant, and concurrent mutation. A unit test of decision arithmetic does not prove distributed invalidation.
