# Security Policy

## Reporting a vulnerability

Please report security issues privately. Do not open a public issue, pull request or discussion.

Use GitHub's private vulnerability reporting on this repository (**Security** tab, **Report a vulnerability**). Include:

- the affected service or library and the commit you tested;
- reproduction steps or a proof of concept;
- the impact you expect (for example cross-tenant data access, authentication bypass, privilege escalation).

You should get an acknowledgement within a few working days. We will agree a disclosure date with you once a fix is ready.

## Supported versions

This template is pre-1.0. Only the latest commit on `main` receives security fixes.

## Scope

In scope: the services, shared libraries, gateway, Aspire AppHost and Helm charts in this repository.

Out of scope:

- vulnerabilities in third-party dependencies (report those upstream; tell us if a fix needs a version bump here);
- local development defaults that only apply in the Development environment, such as plaintext AMQP to the local container;
- deployments that change the security-critical configuration this template validates at startup.

The design principles behind the template's security model are described in [docs/security.md](docs/security.md).
