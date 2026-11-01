# Test Selection

Choose evidence by the behavior being proved.

| Behavior | Minimum useful evidence |
| --- | --- |
| Pure domain invariant | focused xUnit test with real entity/value object |
| Application orchestration | application service with owned dependencies; mock only external boundaries |
| Authorization or tenant isolation | denied and allowed tests through the owning policy/context path |
| EF query, constraints, transactions | SQLite or PostgreSQL; not EF InMemory alone |
| PostgreSQL-specific mapping or SQL | Testcontainers PostgreSQL |
| Valkey protocol, expiry, or distributed state | `Shared.Valkey.IntegrationTests` or owning integration fixture |
| RabbitMQ/Wolverine transport | transport-accurate test; `Shared.EventBus.Wolverine.Tests` uses Testcontainers |
| MassTransit selection | provider-specific registration and behavior tests when that provider is changed |
| HTTP middleware, binding, status codes | `WebApplicationFactory` or `TestServer` through the real pipeline |
| Project dependency direction | TngTech.ArchUnitNET.xUnit architecture test |
| AppHost resource wiring | static AppHost tests unless live orchestration is explicitly approved |

## Expansion Triggers

Add adjacent projects only when a change crosses their boundary: Auth and Identity; permissions and a service policy; UoW and EF event boxes; cache invalidation and consumers; event contract and handler; gateway route and target service.

## Iteration

Use `--filter FullyQualifiedName~NameFragment` while iterating. Before completion, run the full affected test project without a filter. Build the solution when contracts, project references, registration, or public APIs changed.
