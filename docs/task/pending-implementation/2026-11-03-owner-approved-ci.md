# Owner-approved CI for main

Status: Pending implementation

## Summary

Require pull requests for main, including for the sole maintainer. Only the maintainer may authorize CI, and approval applies to the reviewed revision.

## Source basis and boundaries

GitHub repository settings, the existing solution and tests documented in testing.md, and offline Helm checks. No application schema, API, event, or runtime permission changes.

## Phase 0

Use a PR-only ruleset with no bypass actors. The sole writer is the repository owner. GitHub PR review counts cannot enforce solo approval on owner-authored PRs. Use a maintainer-started workflow loaded from protected main, targeting a specific PR revision. Separate read-only execution of PR code from publishing the required status. Fork PR jobs must have no repository secrets or write credentials. Require current checks on main before merge, and confirm that the required result cannot be supplied by a fork workflow. The optional licensed integration tests need a separate safe execution decision.

## Implementation and acceptance

1. Activate and read back the PR-only ruleset, including force-push and deletion restrictions.
2. Bootstrap the trusted workflow through a maintainer PR. Verify that a fork PR and an owner PR remain untested until approved, that failing tests block merge, and that updating a PR requires fresh approval and a new successful check.
3. Require the verified check and document the maintainer workflow in the testing guide.

The bootstrap PR was merged before activating the required check. The workflow is active on main, the ruleset requires the GitHub Actions status on an up-to-date branch, and fork-proposed workflows require maintainer approval. Live verification is pending the owner dispatching CI for a reviewed revision.
