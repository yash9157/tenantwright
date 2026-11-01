# Quality Gate Policy

## Focused Verification

Use the smallest checks that prove the changed behavior:

- `dotnet build TenantWright.slnx` for solution compile coverage;
- focused `dotnet test tests/<Project>/<Project>.csproj` for affected behavior;
- `dotnet test TenantWright.slnx` when broad test coverage is justified;
- targeted Python tests or PowerShell verification scripts for tooling and charts;
- manual relative-link checks for documentation;
- `git diff --check`, scoped diff review, and final status.

Run backend commands from `backend/` unless the command is workspace-root tooling.

## Full Quality Gates

`backend/scripts/run-quality-gates.ps1` is the repository Quality Gates entry point. Run it only when the user explicitly says "Run Quality Gates". Do not infer permission from requests to test, verify, finish, or prepare a release.

When authorized, run it from `backend/`, capture fresh output, and report optional or skipped portions exactly. Do not weaken parameters merely to obtain a passing result.

## Signoff Evidence

Record commands run, results, checks not run, environment limitations, and residual risk. A passing build does not replace behavior tests, and static chart checks do not prove a live rollout.
