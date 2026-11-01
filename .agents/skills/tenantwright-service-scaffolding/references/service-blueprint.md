# Service Blueprint

## Current Shapes

Identity, Tenant, Permissions, AuditLogs, and Notifications use the standard six-project service family and are listed in `TenantWright.slnx`. Workflows is an intentional current exception with host, Application, Application.Contracts, and EntityFrameworkCore projects; it owns Temporal onboarding orchestration and has no Domain or Domain.Shared project. Auth and ApiGateway are separate application boundaries, not standard service families.

Verify this inventory from `TenantWright.slnx` and `services/` before structural work.

## Standard Six Projects

1. `ServiceName` — web host and composition root.
2. `ServiceName.Application` — use-case orchestration and application services.
3. `ServiceName.Application.Contracts` — DTOs, requests, responses, and public application interfaces.
4. `ServiceName.Domain` — aggregates, entities, value objects, rules, and domain services.
5. `ServiceName.Domain.Shared` — cross-layer enums, constants, error codes, and permission definitions.
6. `ServiceName.EntityFrameworkCore` — DbContext, mappings, migrations, and PostgreSQL integration.

Create all six for an ordinary new service. Do not add extra helper, infrastructure, or shared projects to avoid placing code correctly.

## Layer Rules

### Host

Owns controllers, composition, middleware, authorization setup, options binding, startup validation, API description, health endpoints, and bootstrap wiring. It does not own business rules, EF queries, or DTO definitions.

### Application

Owns use cases, commands, queries, validation, coordination, and bounded transaction orchestration. Concrete collaborators are valid for one-implementation internal details. It may use its own EF project only when a real query, projection, or transaction path requires it.

### Application.Contracts

Owns HTTP DTOs and public application abstractions. Distributed `*Eto` contracts belong in `Shared.EventContracts`, not this layer.

### Domain and Domain.Shared

Domain owns invariants and behavior. Domain.Shared contains dependency-light values shared across this service's layers. Neither owns host or EF behavior.

### EntityFrameworkCore

Owns provider-specific persistence, mappings, migrations, and DbContext composition. It does not own application orchestration.

## Example

```text
services/ServiceName/
  ServiceName/
  ServiceName.Application/
  ServiceName.Application.Contracts/
  ServiceName.Domain/
  ServiceName.Domain.Shared/
  ServiceName.EntityFrameworkCore/
tests/ServiceName.Tests/
```

Add projects to `TenantWright.slnx`, compose the host in Aspire when required, add focused tests, and update `docs/adding-a-service.md`. Do not start Aspire without approval.
