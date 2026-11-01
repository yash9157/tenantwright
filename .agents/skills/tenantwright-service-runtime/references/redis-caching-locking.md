# Valkey Caching and Distributed Locking

Valkey is the runtime store. Compatible package and API names may still contain `Redis`, including StackExchange.Redis and DistributedLock.Redis.

## Inspect

- `shared/Shared.Caching/**` for HybridCache composition and key ownership.
- `shared/Shared.DistributedLocking/**` for lock policies and acquisition semantics.
- The consuming module, options, outage behavior, and focused tests.

## Cache Rules

- Choose local, distributed, or hybrid caching from consistency needs.
- Make keys deterministic, versionable, environment-safe, service-safe, and tenant-safe.
- Define TTL, invalidation, miss, stale-value, deserialization, and Valkey-outage behavior.
- Prevent stampedes and unbounded key cardinality.
- Delay transactional cache changes until UoW completion when consistency requires it.
- Do not cache secrets, raw tokens, full payloads, or unbounded collections.
- Pub/sub is a wake signal, not a durable business event.

## Lock Rules

Use the existing `IDistributedLock` APIs and named policies. Prefer database constraints, idempotency, or transactions when they solve the problem without a lock. Otherwise normalize and tenant-scope the lock name, bound acquisition and lease times, keep protected work short, propagate cancellation, and define lock-loss behavior. Mandatory ownership fails closed.

A lease does not make external effects idempotent. Do not hold a lock across unrelated network calls, and do not treat an in-memory lock as multi-instance ownership.

## Verification

Use focused shared and consuming-service tests for key shape, invalidation, outage, contention, lease loss, and tenant separation. A fake proves caller behavior only; atomicity and lease claims require real Valkey evidence.
