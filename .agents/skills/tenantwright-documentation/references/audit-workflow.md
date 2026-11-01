# Documentation Audit Workflow

## Scope

Choose affected pages from the changed source boundary. Do not turn a focused update into a repository-wide audit without an explicit request.

## Evidence

For each claim, inspect the smallest authoritative source: project files, implementations, tests, configuration, migrations, charts, or scripts. Classify the claim as verified, planned, stale, or not verified.

## Reconcile

- Correct stale behavior, commands, paths, ownership, and configuration names.
- Remove references to deleted assets rather than preserving historical instructions.
- Keep planned capabilities clearly separated from current behavior.
- Update task records only for material scope, decision, status, acceptance, or handoff changes.

## Validate

- Run documented non-live commands when practical.
- Check changed relative links manually or with a local PowerShell `Test-Path` sweep.
- Confirm code snippets contain no credentials or environment-specific sensitive values.
- Review the final diff for duplicate guidance and unsupported claims.

Report exactly what was checked and what could not be verified.
