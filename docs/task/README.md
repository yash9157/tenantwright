# Task records

The agent governance (`AGENTS.md`) uses task records to plan and hand off substantial work. Contributors can use the same convention.

## Folders

| Folder | Contents |
|---|---|
| [`pending-implementation/`](pending-implementation/) | Work not yet complete: planned tasks, deferred or blocked decisions, superseded designs, audits. Each record's own status field decides whether it is available work; a record being in this folder doesn't make it available |
| [`done-implementation/`](done-implementation/) | Completed work, moved here once implementation and verification are finished |

## When a record is needed

- **Needs a record:** changes to architecture, tenancy, authorization or permissions, schema, contracts or events, or service ownership. So do cross-repository changes, deployment or runtime-policy changes, and any work that will need a later handoff.
- **No record needed:** small, self-contained, behaviour-preserving fixes, such as wording, links or formatting, that are completed and verified in one change.
- **Check before proposing.** Search both folders for an earlier decision. A recorded deferral is a decision, and reopening it needs explicit approval.

## Pending record contents

Name the file `YYYY-MM-DD-<short-slug>.md` and include:

- summary and why it is needed;
- source basis: the files, tests or docs that motivated the task;
- affected repositories and boundaries;
- **Phase 0 decisions**: the frozen end-state for entities, schema, contracts, events, permissions, routes, operations and tests;
- one implementation phase, split into internal work packages and concrete tasks;
- acceptance checks, blockers and risks, and a verification plan.

Update a record when its scope, decisions, status, blockers or verification change. Don't update it for routine bookkeeping.

## Done record contents

When the work is complete, move the file to `done-implementation/` and add:

- what changed and why, with the completed work packages;
- the changed repositories and boundaries, and important implementation details;
- the verification run and its results, checks not run, remaining risks and follow-up drift;
- a plain-language explanation of before/after behaviour, where data is read and written, and which permissions guard it;
- a `Documentation Sync:` line: `Updated ...`, `Checked, no docs change needed ...`, or a user-approved `Deferred ...`.

Keep records useful for handoff: enough detail for someone else to continue safely, but no transcripts or full command logs.
