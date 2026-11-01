# API Contract Rules

## Placement

- Put request, response, DTO, and public application interfaces in the owning `*.Application.Contracts` project.
- Keep domain entities, EF types, host composition types, and implementation models out of public contract surfaces.
- Keep controllers thin; they own HTTP binding, route metadata, and boundary authorization, then delegate.

## Evolution

- Inspect every real caller before changing a route, property, nullability rule, enum, or status code.
- Prefer the best final shape and migrate all in-repository callers in the same change.
- Never repurpose a field or enum member under its old name.
- Remove superseded shapes after migration.
- Add versioning or compatibility only for a named real consumer and a defined removal condition.
- Keep OpenAPI, examples, error responses, and tests aligned.
- A future generated client must derive from the reviewed OpenAPI; it must not become a second hand-maintained contract.

## Boundary Rules

Validate authentication, authorization, tenant scope, input shape, pagination limits, filtering, sorting, and serialization at the correct boundary. Input validation does not replace domain invariants, concurrency checks, or database constraints.

Use `tenantwright-auth-identity-flow` for token and login meaning, `tenantwright-permissions-authorization` for capability semantics, `tenantwright-service-scaffolding` for assembly placement, and `tenantwright-service-runtime` for transport or host internals.

## Review Questions

- Who owns the meaning, and which callers consume it?
- Does the contract expose internal persistence or runtime types?
- Are omitted, null, empty, and unknown enum values deliberate?
- Do status codes and ProblemDetails remain stable and non-sensitive?
- Are collection sizes and query costs bounded?
- Do OpenAPI and focused tests match the actual wire shape?
