# Source and Handoff

## Source Order

1. Current implementation and tests.
2. Configuration, project references, migrations, charts, and scripts.
3. Existing records under `docs/task/`, including completed, deferred, and blocked decisions.
4. Canonical flat documentation under `docs/`.
5. Official third-party documentation when a current capability, version, or licence affects the decision.

Do not infer demand from missing code. Surface any recorded deferral before recommending that work be reopened.

## Handoff Contract

A decision is ready for planning when it states:

- outcome and scope;
- owning boundary and affected callers;
- selected option and rejected alternatives;
- tenant, authorization, data, contract, and operational implications;
- assumptions that still require validation;
- complete final shape and non-goals;
- specialist skills needed for implementation;
- focused acceptance and verification evidence.

If a material choice remains unresolved, ask for that decision rather than hiding it in an implementation plan.
