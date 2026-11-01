# Governance Escalation Triggers

Escalate before implementation when a choice changes any of these boundaries:

- tenant resolution, isolation, or fail-closed behavior;
- authentication, authorization, permission semantics, or credential handling;
- service ownership or a cross-service implementation dependency;
- public API or event contracts;
- shared infrastructure responsibilities;
- persistent schema, migration lineage, database ownership, or destructive data handling;
- deployment architecture, secret management, network policy, or runtime provider;
- restrictive or preview dependencies;
- authoritative governance, task scope, or release acceptance.

## Required Decision

State the proposed change, source evidence, affected owners and callers, security and operational impact, alternatives, recommendation, and exact approval needed.

## No Escalation Needed

Proceed with low-risk, reversible edits when the owner and expected outcome are clear, existing patterns apply, and no boundary above changes. Still preserve unrelated work and verify the result.
