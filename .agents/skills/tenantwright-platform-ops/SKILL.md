---
name: tenantwright-platform-ops
description: Operate and review Aspire, container, Helm, and deployment assets safely.
---

# TenantWright Platform Operations

Use this skill for deployment-plane work: Aspire orchestration, host images, Helm charts, environment overlays, cluster resources, promotion tooling, and deployment verification.

## Source Map

Read [references/platform-ops-map.md](references/platform-ops-map.md) before changing platform assets. Current owners are:

- local orchestration: `backend/orchestration/TenantWright.AppHost/`;
- common runtime defaults: `backend/orchestration/TenantWright.ServiceDefaults/`;
- host image definitions: Dockerfiles beside Auth, gateway, and service hosts;
- charts and overlays: `backend/charts/`;
- deployment selection and promotion tools: `backend/tools/deployment/`;
- platform verification: `backend/scripts/verify-*.ps1`.

No CI workflow definitions are checked in yet. Treat the deployment Python tools as tested building blocks, not evidence that an automation pipeline currently invokes them.

## Guardrails

- Obtain explicit user approval before starting the AppHost, containers, a cluster operation, or any Helm install or upgrade.
- Never print credentials, generated local secrets, messaging material, kubeconfig content, or rendered secret values.
- Keep tenant isolation, network policy, authorization, and secret references fail closed.
- Use immutable image digests for promotions and preserve migration-lineage safety.
- Do not treat a successful rollout as proof that database changes can be rolled back.
- Name new resources with the existing scheme: `TenantWright.*` projects, `tenantwright-*` containers, charts, and principals, and `tenantwright_*` databases.

## Workflow

1. Classify the change as local orchestration, image, chart, environment, infrastructure dependency, or deployment tooling.
2. Inspect the owning source plus every overlay and caller affected by the change.
3. Freeze rollback, secret, migration, dependency-order, and health-check behavior before editing.
4. Make the smallest complete change, including tests and verification scripts.
5. Run static checks first: focused Python tests, PowerShell verifiers, `helm lint`, or `helm template` as applicable.
6. Run live operations only with explicit approval and report exactly what was started or changed.

## Verification

Prefer source-local checks. Build service hosts when image or publish behavior changes. Run deployment-tool unit tests for Python changes. Use the matching `backend/scripts/verify-*.ps1` checks for chart policy. State any cluster or live rollout checks not run.
