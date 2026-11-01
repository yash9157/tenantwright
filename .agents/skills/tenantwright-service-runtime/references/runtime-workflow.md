# Runtime Source Trace

## Trace Order

1. Start at the endpoint, application service, message handler, hosted service, or public shared abstraction.
2. Follow module and DI registration, project references, and service lifetimes.
3. Follow typed options from `appsettings*.json` and environment keys through validation to the consumer.
4. Identify external dependencies, timeouts, retry budgets, idempotency, cancellation, shutdown, and health behavior.
5. Follow tenant, principal, and correlation context into distributed state and outgoing calls.
6. Read focused tests and affected flat documentation before editing.

Run from `backend` and narrow the paths:

```powershell
git grep -n -E "ConfigureServices|AddOptions|ValidateOnStart|AddHttpClient|AddHealthChecks|AddOpenTelemetry" -- app services shared orchestration
git grep -n -E "ProjectReference|PackageReference" -- "*.csproj"
git grep -n -E "Timeout|Retry|Lease|Provider|ConnectionStrings" -- "*.cs" "appsettings*.json"
```

Read versions from `Directory.Build.props`, `Directory.Packages.props`, and project files. Source registration and consumers, not package presence, establish active behavior.

## Ownership Decision

Keep one-service policy local. Extend an existing `Shared.*` project only for provider-neutral infrastructure with real multiple-service use. A new shared owner or infrastructure package requires governance approval.

For every external dependency, prove missing-configuration behavior, startup validation, bounded retries, replay safety, tenant isolation, cancellation, health signals, and process-loss recovery. Never infer those properties from a library name.
