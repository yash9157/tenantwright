# Request Validation

Inspect the owning request, all callers, validator registration, invocation, ProblemDetails mapping, and tests.

```powershell
git grep -n -E "FluentValidation|AbstractValidator|IValidator|ValidateAsync|AddValidatorsFromAssembly|ModelState" -- app services shared tests Directory.Packages.props
git grep -n -E "InvalidModelStateResponseFactory|ValidationError|ProblemDetails" -- shared app services
```

## Place Rules by Meaning

| Rule | Owner |
|---|---|
| required values, format, range, bounded collections, pure cross-field consistency | request validator or HTTP binding boundary |
| route/query conversion and missing body | HTTP binding and contract configuration |
| authentication, authorization, or tenant context | security boundary, never only a validator |
| current state, uniqueness, external eligibility | application or domain logic with concurrency handling |
| aggregate validity and database uniqueness | domain and persistence constraints |

Keep validators in the owning application feature unless an existing host-specific request flow clearly owns them. Keep Contracts and Domain dependency-light. Do not add a shared validation pipeline without approved ownership and multiple real callers.

## Invocation and Errors

- Await `ValidateAsync` and propagate cancellation.
- Register validators deterministically with correct lifetimes; required validation must not disappear when registration is missing.
- Use existing typed errors and ProblemDetails. Preserve canonical error codes, JSON property names, nested paths, and multiple failures.
- Do not include attempted secret or sensitive values in errors or logs.
- Keep pure validation free of writes and remote calls.
- Validation success is not a concurrency guarantee and does not replace commit-time constraints.
- Verify OpenAPI requiredness when annotations or nullability change.

Test valid, invalid, boundary, null, nested, collection, cancellation, DI-resolution, and pre-side-effect invocation paths. Separately test unchanged authorization and tenant isolation.
