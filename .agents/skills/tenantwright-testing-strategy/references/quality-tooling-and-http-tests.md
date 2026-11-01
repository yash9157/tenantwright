# Quality Tooling and HTTP Tests

## Architecture and Static Checks

TngTech.ArchUnitNET.xUnit is centrally available for dependency-direction and layering rules. Use architecture tests for stable structural policies, not incidental implementation details.

Builds, analyzers, formatting, and `git diff --check` complement behavior tests. They do not replace them.

## HTTP Pipeline Tests

Use the existing `WebApplicationFactory` or `TestServer` pattern when the behavior depends on routing, model binding, authentication, authorization, middleware ordering, exception mapping, headers, or status codes.

Exercise the real production registration with the smallest deliberate replacements. Do not bypass `SessionValidationMiddleware`, `GlobalExceptionHandlingMiddleware`, tenant resolution, or policy registration when those are part of the assertion.

Cover as applicable:

- anonymous, invalid-token, denied, and allowed requests;
- wrong tenant, host/tenant mismatch, and missing context;
- malformed body, route, query, and concurrency token;
- cancellation and dependency failure;
- safe error bodies without stack traces or sensitive values.

## Gateway and AppHost

`Gateway.Tests` owns YARP routing and gateway policy checks. `TenantWright.AppHost.Tests` should prefer static orchestration-model checks. Starting AppHost or external infrastructure requires explicit user approval.

## Quality Gates

Focused builds and tests are normal verification. `scripts/run-quality-gates.ps1` is reserved for the exact user instruction "Run Quality Gates".
