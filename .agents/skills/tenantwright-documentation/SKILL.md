---
name: tenantwright-documentation
description: Create and audit source-grounded documentation for the backend template.
---

# TenantWright Documentation

Use this skill for substantial technical documentation, material behavior or ownership updates, detailed task plans, and documentation audits. Small wording, link, or formatting fixes can use direct inspection.

## Canonical Layout

Canonical documentation is the flat workspace-root `docs/` tree. Read [references/docs-target-structure.md](references/docs-target-structure.md) before choosing a target. Do not create parallel documentation inside `backend/`.

## Workflow

1. Define the audience, question, owning boundary, and source-of-truth files.
2. Verify behavior in code, configuration, tests, migrations, charts, and scripts. Mark unverified claims explicitly.
3. Update the smallest applicable flat page; use sections rather than creating owner-specific folder trees.
4. Use [references/cross-cutting-templates.md](references/cross-cutting-templates.md) for service, shared-library, and cross-service sections.
5. For broad accuracy work, follow [references/audit-workflow.md](references/audit-workflow.md).
6. Check commands from their documented working directory and check every changed relative link manually. A simple PowerShell `Test-Path` sweep is acceptable.
7. Keep task status under `docs/task/` synchronized when scope, decisions, acceptance, or completion materially changes.

## Writing Rules

- Source is authoritative; documentation describes current behavior and clearly labels planned work.
- Explain ownership, entry points, data flow, tenancy, authorization, failure behavior, configuration, operations, and verification where relevant.
- Never include credentials, connection strings, tokens, generated local secrets, or sensitive example values.
- Call the product TenantWright. Write identifiers exactly as the source spells them (`TenantWright.*`, `tenantwright-*`, `tenantwright_*`).
- Prefer stable paths and symbol names over copied implementation blocks.
- Keep examples neutral and safe for a reusable multi-tenant SaaS template.

## Completion

Report pages changed, authoritative sources checked, commands or links verified, known gaps, and any task-record update. Do not claim a page is current when affected behavior remains unverified.
