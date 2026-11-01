# Section Templates

Use these as prompts, not mandatory headings.

## Service Section

- responsibility and non-goals;
- projects and layering;
- public APIs or events;
- tenant and authorization boundaries;
- persistence, background processing, and dependencies;
- configuration and operational failure modes;
- focused tests and commands.

Place service architecture in `docs/architecture.md`; route configuration, messaging, tenancy, security, deployment, and testing details to their matching flat pages.

## Shared Infrastructure Section

- problem solved and supported consumers;
- abstractions versus provider-specific implementations;
- registration and configuration;
- lifecycle, concurrency, retries, and failure behavior;
- extension constraints and focused tests.

Shared code must remain infrastructure-only and must not acquire service domain rules.

## Cross-Service Flow Section

- initiating actor or process;
- ordered participants and owned state;
- API or event boundaries;
- tenant and authorization checks;
- idempotency, timeout, compensation, and recovery;
- telemetry and acceptance evidence.

Describe the flow in the most relevant flat topic page rather than creating a separate folder hierarchy.
