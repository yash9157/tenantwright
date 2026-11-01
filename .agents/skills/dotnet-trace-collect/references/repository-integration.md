# Repository Diagnostic Collection Boundaries

Apply workspace and backend governance before any command in this package. Upstream commands are examples, not permission to install tools, launch hosts, attach to processes, modify deployments, or expose diagnostic endpoints. This reference overrides conflicting defaults in bundled references.

## Discover the Owner

Use source and approved incident context first:

```powershell
rg --files backend/services backend/app backend/gateway backend/orchestration -g "*.csproj" -g "Dockerfile*"
rg -n "TargetFramework|ManagePackageVersionsCentrally" backend/Directory.Build.props backend/Directory.Packages.props
rg --files backend/charts
```

Identify the service, running build/runtime, deployment, symptom, and owning task or runbook. Use `tenantwright-observability-operations` for telemetry and incident context, `tenantwright-service-runtime` for instrumentation, and `tenantwright-platform-ops` for deployment changes. Do not infer that the shell machine is the target or that a project target framework equals the running process version.

Check installed tool help/version and current official documentation before using version-dependent profiles, provider keywords, flags, kernel prerequisites, or monitor APIs. Adding custom providers may replace defaults, so retain required runtime/thread/CPU events explicitly when supported.

## Capture Approval and Limits

Before live collection, obtain approval for the exact environment, service/process or pod/container, duration, providers/profile, privileges, and protected output location. A process may serve multiple tenants; PID scope is not tenant isolation. Console or cluster access is not capture approval.

- Revalidate process identity immediately before capture; never assume PID 1 or reuse a stale PID.
- Prefer the least-privileged tool that answers the question. Machine-wide or host ETW collection needs explicit approval.
- Do not add privileged containers, shared process namespaces, capabilities, sidecars, image tools, environment variables, or exposed ports without platform/security approval.
- Never use `dotnet-monitor --no-auth`. Keep authenticated, constrained access and do not expose diagnostic sockets or APIs publicly.
- Bound duration, buffers, disk use, overhead, and cancellation. Stop if health degrades, scope differs, or storage limits are reached.
- Do not download helpers or globally install/update tools automatically.
- Stop only collectors started for this task, never the observed application. Confirm collector shutdown and remove temporary listeners.

## Protected Artifacts

Treat traces, ETL files, speedscope files, and dumps as sensitive. URLs, paths, exceptions, identifiers, payloads, credentials, or topology may appear even when normal logging avoids them. Agree on access-restricted local storage, retention, and recipients before collection.

Use minimal providers and synthetic reproduction when sufficient. Do not place raw artifacts under tracked source or canonical docs, print them into chat/logs, commit them, publish them, or upload them to web viewers or cloud services without explicit authority. Renaming is not sanitization. Share only reviewed, sanitized findings. Delete only specifically approved artifacts after exact-path verification.

## Scope of This Skill

This package does not include dump collection. Do not silently install another skill or collect process memory as a fallback. Dump workflows require separate privacy and impact review.

Collection alone does not establish root cause or authorize a fix. Source review, microbenchmarks, trace analysis, and production telemetry are separate workflows.

## Verification and Handoff

Record sanitized target/build/runtime/tool version, command with secrets removed, UTC start/end, actual providers and duration, protected output path and size, capture errors or event loss, target impact, and collector shutdown. Verify that the artifact exists and is readable by the approved local analysis tool. Keep metadata and sanitized findings in the owning task, never raw traces in canonical documentation.
