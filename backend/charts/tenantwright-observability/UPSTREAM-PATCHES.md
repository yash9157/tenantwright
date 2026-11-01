# Vendored upstream chart patches

The wrapper keeps two upstream charts as narrow local vendors because their
published templates cannot render all production images by immutable digest.
No application behavior is forked.

## `tempo-distributed` `1.61.3-tenantwright.1`

Upstream source: Grafana Helm chart `tempo-distributed` `1.61.3`
(Tempo `2.9.0`).

TenantWright patch:

- add `tempo.image.digest`, component digest inheritance, and `repository@digest`
  rendering;
- correct the gateway image helper to use the component image map;
- retain all other upstream templates and embedded dependencies unchanged.

The upstream chart is marked deprecated. TenantWright therefore treats this as a
version-locked Tempo 2 production bridge, not an evergreen dependency.
Upgrading distributed Tempo to Tempo 3 requires a separately approved
architecture and migration because Tempo 3 microservices mode introduces a
Kafka-compatible ingest dependency and has no downgrade path.

## `promtail` `6.17.1-tenantwright.1`

Upstream source: Grafana Helm chart `promtail` `6.17.1`.

TenantWright patch:

- add `image.digest`;
- render the Promtail image as `repository@digest` when supplied.

Promtail remains the node container-log collector for this chart. Replacing it
with Alloy is a separate collector migration and must not be mixed into an
unrelated chart update.

## Refresh procedure

1. Download the exact intended upstream archive.
2. Replace only the matching vendor directory.
3. Reapply and review the patches above.
4. Increment the `-tenantwright.N` chart version.
5. Run `helm dependency update charts/tenantwright-observability`.
6. Run `scripts/verify-observability-platform.ps1`.
7. Review rendered image, RBAC, NetworkPolicy, PDB, storage, and Secret diffs
   before promotion.
