#!/usr/bin/env python3
"""Verify the telemetry Helm chart without requiring a platform-specific shell."""

from __future__ import annotations

import argparse
import re
import subprocess
from pathlib import Path


def render_chart(helm_path: str, chart_path: Path, *values: str) -> str:
    command = [helm_path, "template", "tenantwright-telemetry", str(chart_path), "--namespace", "tenantwright-telemetry"]
    for value in values:
        command.extend(["--set", value])
    result = subprocess.run(command, check=False, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"Telemetry chart render failed (exit code {result.returncode}).")
    return result.stdout


def require_markers(rendered: str, markers: tuple[str, ...], section: str) -> None:
    for marker in markers:
        if marker not in rendered:
            raise RuntimeError(f"{section} is missing required marker: {marker}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--helm-path", default="helm")
    args = parser.parse_args()

    chart_path = Path(__file__).resolve().parents[1] / "charts" / "tenantwright-telemetry"
    try:
        lint = subprocess.run(
            [args.helm_path, "lint", "--strict", str(chart_path)],
            check=False,
            capture_output=True,
            text=True,
        )
        if lint.returncode != 0:
            raise RuntimeError(f"Telemetry chart lint failed (exit code {lint.returncode}).")

        default_rendered = render_chart(args.helm_path, chart_path)
        require_markers(
            default_rendered,
            (
                "kind: DaemonSet",
                "name: tenantwright-fluent-bit",
                "name: tenantwright-otel-collector",
                "name: tenantwright-prometheus",
                "kind: NetworkPolicy",
                "kind: PodDisruptionBudget",
                "file_storage",
                "transform/redact",
                "tenantwright-opensearch-credentials",
            ),
            "Default chart",
        )
        require_markers(
            default_rendered,
            (
                "name: tenantwright-prometheus",
                "kind: Ingress",
                "api.dev.example.com",
                'path: "/prometheus"',
                "nginx.ingress.kubernetes.io/auth-type: basic",
                "tenantwright-prometheus-basic-auth",
                "letsencrypt-prod",
                "name: tenantwright-prometheus-ingress",
                "port: 9090",
                "--web.route-prefix=/prometheus",
            ),
            "Prometheus data-source boundary",
        )
        require_markers(
            default_rendered,
            (
                "name: tenantwright-data-prepper",
                "pipelines.yaml",
                "entry-pipeline:",
                "raw-trace-pipeline:",
                "service-map-pipeline:",
                "otel_traces:",
                "service_map:",
                "index_type: trace-analytics-raw",
                "index_type: trace-analytics-service-map",
                "endpoint: tenantwright-data-prepper:21890",
                "checksum/config:",
                "name: materialize-dataprepper-config",
                "secretName: tenantwright-opensearch-credentials",
            ),
            "Trace Analytics chart",
        )

        alternate_port_rendered = render_chart(args.helm_path, chart_path, "opensearch.port=21443")
        if "ports: [{ protocol: TCP, port: 21443 }]" not in alternate_port_rendered:
            raise RuntimeError("Telemetry NetworkPolicy does not follow the configured OpenSearch port.")

        fallback_rendered = render_chart(args.helm_path, chart_path, "dataPrepper.enabled=false")
        if "name: tenantwright-data-prepper" in fallback_rendered:
            raise RuntimeError("Direct-export fallback unexpectedly contains Data Prepper.")

        for rendered in (default_rendered, fallback_rendered):
            for forbidden in (
                "tenantwright-observability",
                "workflows.example.com",
                "AVNS_",
                "AccountKey=",
                "prometheus.auth.password",
            ):
                if forbidden in rendered:
                    raise RuntimeError(f"Rendered chart contains forbidden marker: {forbidden}")
            if re.search(r"(?im)^\s*password:\s+[A-Za-z0-9+/=]{20,}\s*$", rendered):
                raise RuntimeError("Rendered chart contains an inline password.")
    except FileNotFoundError as error:
        raise RuntimeError(f"Required command not found: {error.filename}") from error

    print("Telemetry chart verification passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
