# Valkey chart upstream patch

`vendor/valkey` is the official `valkey/valkey` chart `0.11.0` source with one
rendering fix, whitespace-only repository hygiene, and a local version suffix.

- Upstream repository: `https://github.com/valkey-io/valkey-helm`
- Upstream chart version: `0.11.0`
- Upstream commit verified on 2026-07-26:
  `21dc1d8c743b6a79745dd11a6e5112912443d62b`
- Local chart version: `0.11.0-tenantwright.1`
- Patch: remove the trailing tab after
  `volumeClaimTemplates[0].apiVersion: v1` in
  `templates/statefulset.yaml`.
- Repository hygiene: remove one trailing space from `README.md` and one extra
  blank line at EOF from `_helpers.tpl` and `init_config.yaml`. These two
  template changes do not alter rendered resources.

The upstream tab survives `helm lint` but makes the rendered replicated
StatefulSet invalid for standards-compliant YAML parsers. Do not add unrelated
changes to the vendored chart. When upgrading, compare against a new exact
upstream release, rerun strict rendered-YAML parsing, and remove this patch if
upstream has fixed the defect. Apart from the changes listed above and the
local version suffix, keep the vendored source identical to upstream.
