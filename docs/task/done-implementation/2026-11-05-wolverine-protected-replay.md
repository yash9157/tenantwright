# Wolverine signed dead-letter replay

Status: Completed and verified locally, 2026-11-05

## Outcome and source basis

Complete the README roadmap's Wolverine signed dead-letter replay item. The
user selected this recommended item and explicitly approved disposable RabbitMQ
and PostgreSQL integration tests and their in-process hosts.

`Shared.EventBus/Messaging/ProtectedReplay.cs` owns authorization, audit,
issuance and single-use IDs. `TenantWrightInboundTrustVerifier` owns trust and
tenant verification. Before this change, `Shared.EventBus.Wolverine` supplied neither
terminal signing nor replay transport/custody verification. Its native RabbitMQ
dead-lettering preserves rejected messages but cannot append a signature.

Owning boundaries: the existing Wolverine adapter, neutral replay contracts,
existing PostgreSQL replay state and inbox, and their tests. No new service,
public HTTP endpoint, permission, event, package, table or migration is needed.

## Phase 0 decisions

- Preserve the neutral trust and persistence owners. Do not introduce another
  replay ledger or relax origin freshness, tenant verification or signatures.
- After ordinary handler retries are exhausted, the adapter signs the raw body
  and original origin chain as terminal custody, publishes a persistent copy to
  the existing endpoint dead-letter exchange with mandatory routing and a
  publisher confirmation, and only then acknowledges the original. A failed or
  ambiguous publication retains the original unacknowledged and retries terminal
  publication with capped backoff until confirmation or host shutdown. Readiness
  reports blocked terminal publication. Never call Wolverine's `DeferAsync`
  (it acknowledges before republishing), or let an exception escape into its
  last-resort acknowledgment path.
- Security rejections, unknown contracts and malformed payloads retain native
  dead-letter quarantine and are not eligible for protected replay.
- The adapter supplies a terminal reader that verifies endpoint ownership,
  terminal signature, raw and canonical payload digests, original producer,
  scope/tenant binding, timestamps and retention before returning replay
  material. It accepts signed snapshots, not arbitrary trusted DTOs. Terminal
  queues remain the storage owner; no new database or operator API is added.
- Replay uses `ProtectedReplayService` and the existing required
  `IProtectedTerminalStore`, `IProtectedReplayAuthorization`,
  `IProtectedReplayAudit`, and durable `IProtectedReplayIdempotencyStore` seams.
  An operator composition supplies storage lookup and authorization/audit policy;
  the adapter supplies validation and confirmed transport. No permissive default
  authorization or automatically provisioned replay credential is introduced.
- Send as the dedicated replay principal to an endpoint-specific exchange,
  including for events, so recovery does not fan out to other subscribers.
  Replay carries the original origin plus the new replay attestation, without
  terminal or broker death headers. Consumers register only a replay custody
  verifier; delayed-redelivery custody remains unsupported.
- An authorization permits one consumer attempt, matching the existing replay
  ledger. A failed replay goes directly to signed terminal custody and requires
  fresh operator authorization and a new ID. It must not enter ordinary retry
  rules after its ID has been consumed. This also handles ambiguous outcomes
  without weakening single-use protection or introducing cross-database atomicity.
- Ordinary retries and native quarantine remain unchanged. Duplicate signed
  terminal copies are possible after an ambiguous confirmation; message/endpoint
  inbox deduplication remains authoritative for business effects.
- Bound new broker publication waits and propagate cancellation. Log only
  endpoint/message identifiers and fixed failure categories, never payloads,
  credentials or exception messages containing business data.

## Single implementation phase

1. Add signed terminal publication/verification and protected replay transport;
   wire provider registrations, endpoint exchanges, terminal failure policy and
   single-attempt replay failures.
2. Add broker-free denial tests and real RabbitMQ/PostgreSQL proofs: failed
   handler -> signed terminal -> authorized replay -> one tenant-scoped effect;
   event recovery to one endpoint; duplicate/forged/expired/wrong-destination
   replay denial; publication failure and cancellation preserve the original.
3. Sync README, messaging/security/testing guidance and affected skill guidance.
   Review all affected consumers and run skill sync/verification if skills change.

## Acceptance and verification

Baseline: full Wolverine test project and broker-free Shared.EventBus tests.
Final: full affected test projects, static AppHost wiring tests, solution build,
relevant offline chart checks, relative-link checks and `git diff --check`.
Do not run full Quality Gates without the exact user instruction. No AppHost,
persistent database or deployed environment is started or changed.

## Results, data ownership and authorization

Completed all implementation work packages. Wolverine now supplies a verified
terminal reader, signed/confirmed terminal publication, protected replay
transport and replay custody verification. Every receive endpoint has its own
exchange for targeted recovery. The provider-selection tests assert the new
registrations without enabling delayed-redelivery custody.

Before: a failed Wolverine message could only enter unsigned native quarantine,
and consumers rejected replay custody. After: exhausted handler failures enter
signed custody, an explicitly authorized and audited replay can target their
original endpoint, and business effects still run under the original tenant in
the existing inbox transaction. Security failures and malformed messages remain
in unsigned quarantine. An authorized replay permits one consumer attempt; a
failed replay receives a new terminal record and needs fresh authorization.

Data stays in existing locations: terminal snapshots in the endpoint's broker
dead-letter queue, replay issuance/consumption in the destination service's shared
`MessagingReplayState` table, and business writes/inbox records in the service's
resolved shared or dedicated tenant database. No new schema or migration was
introduced. There is no new HTTP endpoint or permission; operator-owned
`IProtectedReplayAuthorization` and `IProtectedReplayAudit` remain required.
Consumers must trust the separately provisioned replay public key, and the
operator must use restricted broker credentials and the destination ledger.

The terminal publisher retains the same terminal ID, signature and raw body
across confirmation retries during one delivery. Publication attempts are bounded
to 30 seconds with backoff capped at 30 seconds. Blocked publication makes
readiness unhealthy. Cancellation links both Wolverine runtime cancellation and
host application stopping so shutdown can drain while leaving the original
unacknowledged for a replacement consumer.

## Verification

- Baseline: Wolverine test project, **29 passed**, including its disposable
  RabbitMQ/PostgreSQL fixtures; broker-free Shared.EventBus tests, **140 passed**.
- Final `dotnet test tests/Shared.EventBus.Wolverine.Tests/Shared.EventBus.Wolverine.Tests.csproj --no-restore`:
  **61 passed**, none skipped. Includes signed command recovery, endpoint-only
  event replay, authorization denial, duplicate-ID/duplicate-publication rejection,
  fresh authorization after a failed replay, tenant/scope mismatch denial,
  tampered/expired/revoked snapshots, cancellation, terminal-exchange removal,
  unhealthy/recovered readiness, and shutdown/replacement-consumer recovery.
- `dotnet test tests/Shared.EventBus.Tests/Shared.EventBus.Tests.csproj --no-build --filter 'FullyQualifiedName!~MassTransitRabbitMqIntegrationTests'`:
  **140 passed**.
- `dotnet test tests/Shared.EntityFrameworkCore.Tests/Shared.EntityFrameworkCore.Tests.csproj --no-restore --filter 'Category!=PostgresIntegration&Category!=RabbitMqIntegration'`:
  **136 passed**, including the updated real host-composition registration tests.
- `dotnet test tests/TenantWright.AppHost.Tests/TenantWright.AppHost.Tests.csproj --no-build`:
  **68 passed** (static wiring; no AppHost started).
- Final `dotnet build TenantWright.slnx --no-restore --verbosity quiet`:
  **passed, zero warnings and errors**. Test/build commands ran from `backend/`.
- `backend/scripts/verify-messaging-charts.ps1`: passed offline chart lint,
  rendering, messaging trust and workload-hardening checks.
- `scripts/sync-skills.ps1` and `scripts/verify-skills.ps1`: passed; all **16**
  canonical packages, manifest and local mirrors verified.
- Changed canonical-page relative links resolve; `git diff --check` passed.
  Final Docker inventory contained no remaining `wolverine-it-*` containers.

Intermediate failures were resolved before final verification: exchange removal
exposed unsafe `DeferAsync` behavior; shutdown exposed the need to link host
stopping before Wolverine drains. The latter intermediate test run was stopped
after it stalled in teardown. An overlapping solution build hit a testhost DLL
lock; the final serial build passed. Final affected suites total **405 passed**.

Checks not run: full Quality Gates (not requested), unrelated EF container
suites, licensed MassTransit integration suites, the full solution test suite,
live AppHost/browser checks, dedicated-tenant deployment, and deployed rollout.
No persistent database or deployment was changed. Implementation signoff preceded
the user's subsequent commit-and-push request; delivery uses the feature branch
`codex/wolverine-protected-replay`, without merging to main or deploying.

Documentation Sync: Updated README roadmap, `docs/messaging.md`,
`docs/security.md`, `docs/testing.md`, and the canonical service-runtime messaging
skill reference. Configuration/architecture/deployment pages were checked; no
new configuration, service owner, package, migration or deployment artifact needs
documenting there. Existing unrelated CI roadmap/task-status drift was left alone.

Skills used: repository governance for scope/ownership and signoff; brainstorming
for Phase 0; service runtime and API/event contracts for transport/trust ownership;
testing strategy for broker/relational evidence; documentation for source-grounded
operator guidance and this record.

## Remaining risks and operating constraints

The broker's signed terminal publication must be confirmed before acknowledgment;
Wolverine's built-in last-resort exception handling can acknowledge a message,
so publication failure containment is covered by real failure-injection tests. Replay issuance and
consumption use the destination service's existing shared-database ledger;
operator composition must target that ledger, including for dedicated tenants.
Origin expiry and key revocation continue to prohibit recovery. Existing unsigned
dead letters are deliberately not upgraded into trusted replay material.

Operator authorization, terminal lookup and durable audit adapters are composition
requirements, not permissive defaults supplied by this feature. No deployed
operator tool or management UI is included. Ambiguous broker confirmation can
leave duplicate terminal copies; ambiguous replay issuance/consumption requires
investigation and a new ID, while the original business inbox prevents repeated
committed effects. Dedicated-database rollout was not exercised in this task.

Phase 0 correction from failure-injection evidence: the first draft used
`IEnvelopeLifecycle.DeferAsync` after terminal publication failed. The real
RabbitMQ/PostgreSQL exchange-removal test failed (56 other tests passed).
Inspection of Wolverine 6.51.0's pinned source confirmed that this API acknowledges
before republishing. Replace that design with retained unacknowledged delivery,
bounded publication attempts with capped backoff and unhealthy readiness during
recovery. Host stopping also cancels recovery before Wolverine drains. No migration
or contract shape changes were introduced by these corrections.

Pinned third-party source checked for continuation and acknowledgment semantics:
[Wolverine 6.51.0 RabbitMqEnvelope](https://github.com/JasperFx/wolverine/blob/0f65b9d4869ba20f977fce3c108af16c7aa2d05d/src/Transports/RabbitMQ/Wolverine.RabbitMQ/Internal/RabbitMqEnvelope.cs)
and [HandlerPipeline](https://github.com/JasperFx/wolverine/blob/0f65b9d4869ba20f977fce3c108af16c7aa2d05d/src/Wolverine/Runtime/HandlerPipeline.cs).

Signoff: approved for the adapter scope above, with no deployment claim.
