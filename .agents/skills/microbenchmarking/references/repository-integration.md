# Repository Benchmark Integration

This reference governs the upstream benchmark guidance for this repository. Apply workspace and backend governance; benchmark evidence never relaxes tenant isolation, authorization, transaction, messaging, or domain correctness.

## Choose the Right Measurement

| Question | Use |
| --- | --- |
| Which managed-code implementation allocates less or runs faster? | This skill, with equivalent outputs and realistic synthetic inputs |
| Does a query reduce SQL round trips or improve a database workload? | `tenantwright-postgresql-efcore` plus provider-backed workload evidence |
| Does a bulk write preserve tenant, audit, concurrency, event, and UoW behavior? | Owning tests first; BDN alone cannot answer |
| Why is a running service slow or using CPU/GC unexpectedly? | Explicitly approved collection with `dotnet-trace-collect` |
| What API throughput or latency holds under concurrency? | A separate load-test plan, not a microbenchmark |

Use a concrete caller and decision. Do not turn ordinary CRUD work into a benchmark project.

## Source Discovery and Placement

Run read-only discovery from the workspace root:

```powershell
rg -n "BenchmarkDotNet|ManagePackageVersionsCentrally|TargetFramework" backend/Directory.Build.props backend/Directory.Packages.props backend/tests backend/tools -g "*.props" -g "*.csproj"
rg --files backend -g "*Benchmark*" -g "global.json" -g "dotnet-tools.json"
rg -n "benchmark|performance budget" docs/task/pending-implementation
```

Read the owner, callers, nearest tests, central build/package configuration, and task decision before choosing a harness. Reuse an existing owner when suitable. Keep permanent coverage in an owned benchmark project; put a task-scoped experiment in a named temporary directory with an explicit lifetime. Do not add dependencies to production projects merely to host a benchmark or bypass service layering to reach internals.

Verify the installed SDK/runtime and compatible BenchmarkDotNet API/CLI against current official documentation. Upstream runtime examples and duration estimates are illustrative. Respect central NuGet ownership and record resolved versions; do not install or globally update SDKs/tools implicitly.

## Bounded Execution and Correctness

Installing this skill is not permission to run benchmarks. Define the comparison, input distribution, case count, time budget, output path, and stopping condition first. Starting the AppHost, containers, databases, or other infrastructure requires explicit approval.

- Prove equivalent outputs and invariants before measuring. `--job Dry` verifies setup and execution, not semantic equivalence or speed.
- Keep setup outside the timed operation unless setup is the subject of measurement.
- Use deterministic synthetic inputs and reset mutable state correctly.
- Preserve comparable runtime, build, hardware, security, caching, and persistence semantics between baseline and candidate.
- Use Release, a bounded filter, supported arguments, and a unique artifacts directory.
- Report dispersion and environmental interference; do not turn one noisy run into a service-level claim.
- Keep raw artifacts and sensitive inputs out of commits. Save only sanitized findings needed by the task.

## Verification and Handoff

Use `tenantwright-testing-strategy` for correctness and fixture ownership, plus the EF or UoW skill when those semantics are involved. Report commit/build, package/runtime versions, environment, filters and parameters, baseline/candidate output, dispersion, allocations, artifact locations, and skipped checks. Distinguish harness validation, measured improvement, and workload-level evidence. No measurement means no numerical speedup claim.
