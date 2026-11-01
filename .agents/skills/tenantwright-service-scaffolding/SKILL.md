---
name: tenantwright-service-scaffolding
description: Backend service structure workflow for six-project service families, modules, namespaces, project references, central packages, Shared.* dependencies, thin hosts, solution membership, and Dockerfiles.
---
# Service Scaffolding

Enforce the target service shape and dependency direction from current source.

## Inspect First

1. Read root and backend governance.
2. Inspect `TenantWright.slnx`, the owning project files, module classes, folders, Dockerfile, tests, and recursive references.
3. Always read [service blueprint](references/service-blueprint.md).
4. Load the relevant focused reference: [project graph](references/project-reference-rules.md), [folder layout](references/folder-layout-examples.md), [package and shared ownership](references/package-policy-shared-usage.md), [host layout](references/host-layout-rules.md), or [Dockerfile closure](references/dockerfile-rules.md).

## Required Workflow

1. Classify the change as a new standard service, repair, split, or an explicitly approved special owner.
2. Use the six-project shape for ordinary services: host, Application, Application.Contracts, Domain, Domain.Shared, and EntityFrameworkCore.
3. Preserve feature-first folders and folder-aligned namespaces.
4. Keep hosts thin, contracts in Application.Contracts, orchestration in Application, invariants in Domain, shared domain constants in Domain.Shared, and provider code in EntityFrameworkCore.
5. Validate the complete project/package graph, module composition, solution membership, and Docker restore closure.
6. Remove obsolete files and references. Update `docs/adding-a-service.md` or `docs/architecture.md` when the blueprint changes.

## Hard Rules

- No ABP packages, circular references, direct cross-service implementation references, business rules in hosts, or EF types in public contracts.
- Do not create interfaces only to mirror one concrete implementation.
- Add `Shared.*` references only for APIs the layer directly uses.
- A new shared project, infrastructure package, or layer exception requires explicit governance review.
- Do not normalize Workflows' current four-project special shape into the standard without an approved ownership decision.

## Review Output

Report blueprint drift, dependency/package issues, solution and Dockerfile changes, documentation impact, focused build/test evidence, and checks not run.
