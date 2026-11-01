"""Ensure every gateway destination is deployable, including older values overlays."""
import json
from pathlib import Path
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
CHART = ROOT / "charts/tenantwright-gateway"


class GatewayChartTests(unittest.TestCase):
    def test_every_destination_is_overridden_with_cluster_dns(self):
        config = json.loads((ROOT / "gateway/ApiGateway/appsettings.json").read_text())
        # Existing CI values contain an env map captured before later services existed.
        overlay = {"image": {"digest": "sha256:" + "a" * 64}, "env": {"ReverseProxy__Clusters__identity__Destinations__identity_1__Address":
                           "http://tenantwright-identity:8080/"}}
        result = subprocess.run(
            ["helm", "template", "tenantwright-gateway", str(CHART), "-f", "-"],
            input=json.dumps(overlay), capture_output=True, text=True, check=True,
        )
        for cluster, definition in config["ReverseProxy"]["Clusters"].items():
            for destination in definition["Destinations"]:
                key = f"ReverseProxy__Clusters__{cluster}__Destinations__{destination}__Address"
                with self.subTest(cluster=cluster):
                    match = re.search(r"^  " + re.escape(key) + r": (.+)$", result.stdout, re.MULTILINE)
                    self.assertIsNotNone(match, f"Missing Kubernetes override: {key}")
                    self.assertEqual(json.loads(match[1]), f"http://tenantwright-{cluster}:8080/")


if __name__ == "__main__":
    unittest.main()
