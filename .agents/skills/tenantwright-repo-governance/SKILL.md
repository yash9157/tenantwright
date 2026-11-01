---
name: tenantwright-repo-governance
description: Coordinate repository ownership, escalation, verification, and signoff.
---

# TenantWright Repository Governance

Use this skill for cross-service or cross-repository changes, shared ownership, project and package topology, governance or skill changes, repository-wide audits, and release-readiness decisions.

## Inspect First

1. Read root and affected child `AGENTS.md` files.
2. Inspect Git status in every affected repository and preserve unrelated work.
3. Identify owners, callers, generated artifacts, task records, docs, and security or tenant boundaries.
4. Read [references/governance-escalation-triggers.md](references/governance-escalation-triggers.md).
5. Use [references/review-checklist.md](references/review-checklist.md) for audits and signoff.
6. For skill or governance work, use [references/skill-package-quality.md](references/skill-package-quality.md).
7. Apply [references/quality-gate-policy.md](references/quality-gate-policy.md) when choosing verification.

## Skill Routing

Choose the smallest matching set from the remaining canonical skills. Typical owners are auth and identity, permissions, API and event contracts, PostgreSQL and EF Core, unit of work, background processing, service runtime, service scaffolding, platform operations, observability, testing, documentation, and brainstorming. Do not reference or route to absent skill packages.

## Workflow

1. Define outcome, scope, owner, escalation decisions, and evidence before implementation.
2. Resolve uncertain architecture or product intent with `tenantwright-brainstorming` before writing a detailed plan.
3. For substantial work, freeze Phase 0 decisions and maintain one final-shape implementation phase in the task record.
4. Check fail-closed authorization, tenant isolation, service boundaries, shared infrastructure ownership, migration lineage, and package direction first.
5. Keep every changed line traceable to the outcome or an affected caller, test, document, generated artifact, or cleanup.
6. Run focused checks owned by the affected skills.
7. Run `backend/scripts/run-quality-gates.ps1` only when the user explicitly says "Run Quality Gates".
8. Review final Git state, task status, docs, generated artifacts, and residual risk.

## Signoff

Use `approved`, `approved with follow-up`, or `blocked`. Approve only when every boundary has an owner, hard blockers are resolved, fresh verification matches the risk, and skipped checks or drift are explicit.
