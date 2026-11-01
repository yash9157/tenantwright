# Skill Package Quality

## Canonical Ownership

The canonical skill tree is workspace-root `.agents/skills/`. Workspace-root `scripts/sync-skills.ps1` mirrors it into `.claude/skills/` and `.kiro/skills/` and generates `.agents/manifest.json`. Never edit mirrors or the manifest directly.

Use `scripts/sync-skills.ps1` after canonical skill changes, then `scripts/verify-skills.ps1` before signoff, unless the user has explicitly prohibited those commands for the task.

## Package Contract

Each skill package must have:

- `SKILL.md` with YAML frontmatter whose `name` exactly matches the directory;
- a neutral, single-line frontmatter `description`;
- no more than 500 lines in `SKILL.md`;
- concise, source-grounded instructions;
- `agents/openai.yaml` with quoted `display_name`, quoted 25-64 character `short_description`, and a `default_prompt` containing `$<skill-name>`;
- every remaining `references/*.md` named literally in `SKILL.md` and reachable by a valid relative Markdown link.

## Content Review

- Remove stale services, scripts, paths, tools, and deleted skill routes.
- Keep volatile inventories in source rather than copying them unnecessarily.
- Do not include credentials, environment-specific sensitive values, or instructions that weaken security.
- Ensure examples match the reusable multi-tenant SaaS template.
- Delete orphaned references instead of keeping unlinked material.

## Verification

Run the repository verification script when allowed, then independently inspect frontmatter, line counts, reference linkage, relative links, prohibited terms, and final Git diff. Report when mirror synchronization or verification was intentionally not run.
