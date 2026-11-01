# Host Layout Rules

The host is a composition and HTTP boundary, not a business layer.

## Allowed

- `Program.cs` and module bootstrap;
- controllers or endpoints;
- DI and middleware registration;
- authentication and authorization policy wiring;
- options binding and startup validation;
- API description and health endpoints;
- bootstrap seeding composition;
- provider-neutral messaging host registration.

## Forbidden

- business invariants or use-case orchestration;
- EF queries or direct DbContext use in controllers;
- public DTO ownership;
- provider implementation logic;
- another service's implementation types.

## Suggested Shape

```text
ServiceName/
  Program.cs
  ServiceNameHostModule.cs
  appsettings.json
  Dockerfile
  Controllers/
    FeatureName/
  Configuration/
  Authorization/
  Extensions/
  Middleware/
  Properties/
```

Create optional folders only when code exists. Keep `Program.cs` minimal: build the host, apply shared defaults, register the module, map endpoints, and run. Keep middleware order explicit and security-sensitive options fail-closed.

Controllers should validate HTTP binding, rely on the correct policy, delegate to Application, and map the established response. Flag template samples, flat controller dumps, business logic in startup classes, and host data folders that grow beyond bootstrap composition.
