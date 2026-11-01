---
name: tenantwright-brainstorming
description: Frame uncertain multi-tenant SaaS decisions before planning or implementation.
---

# TenantWright Brainstorming

Use this skill when the outcome, ownership, or approach is genuinely uncertain. It turns an open question into a source-grounded decision that a specialist can plan or implement.

## Do Not Use

Skip this skill for mechanical fixes, routine lookups, or execution of an already approved design unless new evidence changes a material assumption.

## Workflow

1. State the desired outcome separately from any suggested implementation.
2. Inspect current source, configuration, tests, and task records before treating an apparent gap as new work.
3. Identify the owning service or shared boundary, callers, tenant and authorization constraints, and irreversible decisions.
4. Use [references/decision-lenses.md](references/decision-lenses.md) to compare only credible options.
5. Ask the smallest question that resolves a material product or architecture choice.
6. Record the decision, rationale, rejected alternatives, assumptions, and evidence needed to revisit it.
7. Hand off with [references/source-and-handoff.md](references/source-and-handoff.md).

## Decision Rules

- Preserve fail-closed authorization and tenant isolation.
- Prefer the best end-state design; this template has no compatibility obligation to undeployed consumers.
- Do not create duplicate owners, bypass layered boundaries, or move domain behavior into shared infrastructure.
- Distinguish facts verified in source from assumptions and preferences.
- Keep options materially different. Do not manufacture variants that lead to the same design.
- Stop exploring once one option clearly satisfies the outcome and constraints.

## Output

Return a compact decision brief:

- problem and desired outcome;
- source evidence and existing owner;
- constraints and non-goals;
- credible options with tradeoffs;
- recommendation and rationale;
- decisions requiring approval;
- implementation owner and verification evidence.
