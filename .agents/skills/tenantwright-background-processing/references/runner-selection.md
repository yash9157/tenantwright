# Runner Selection

## Current Owners

- `Shared.BackgroundTasks` defines the common job abstractions and execution behavior.
- `Shared.BackgroundTasks.Hangfire` is the durable Hangfire provider. AuditLogs, Identity, and Permissions hosts currently compose it.
- Auth alone uses Quartz for OpenIddict maintenance.
- Workflows alone uses Temporal 1.17. Its current capability is initial-owner onboarding through `InitialOwnerOnboardingWorkflow`, `TemporalWorkflowWorker`, and `TemporalWorkflowStarter`.
- Provider-neutral message delivery belongs to `Shared.EventBus` and is not a substitute job queue.

Discover registration and callers before choosing:

```powershell
git grep -n -E "IBackgroundJob|Hangfire|Quartz|BackgroundService|IHostedService" -- app services shared tests
git grep -n -E "Temporalio|WorkflowRun|WorkflowSignal|StartWorkflowAsync|AddWorkflow" -- services/Workflows tests/Workflows.Tests
git grep -n -E "Cron|Queue|PollInterval|Retry|Timeout" -- app services shared "appsettings*.json"
```

## Selection

Use Hangfire for durable queued or recurring work already inside its host owner. Use Quartz only for Auth's integrated OpenIddict maintenance unless an approved design expands ownership. Use Temporal only for durable cross-service orchestration that belongs in Workflows. Use a hosted service for bounded infrastructure drains or maintenance loops with durable state and explicit health. Use messaging for contract delivery, not as an untracked background executor.

Do not migrate responsibility between these runners without evaluating durability, identity, retries, operator state, persistence, and deployment impact.
