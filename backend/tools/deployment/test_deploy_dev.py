import base64
import gzip
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("deploy_dev", Path(__file__).with_name("deploy_dev.py"))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


class DeploymentTests(unittest.TestCase):
    def test_private_ci_values_cannot_disable_backend_network_policy(self):
        deploy.validate_backend_environment({"networkPolicy": {"enabled": True, "ingressController": {"enabled": True}}})
        for policy in [{"enabled": False}, {"ingressController": {"enabled": False}}]:
            with self.assertRaisesRegex(ValueError, "cannot disable NetworkPolicy"):
                deploy.validate_backend_environment({"networkPolicy": policy})

    def test_disabled_private_policy_stops_ci_before_any_helm_command(self):
        release = "tenantwright-auth"
        values = {release: {"image": {"repository": "yash9157/" + release}, "networkPolicy": {"enabled": False}}}
        environment = {
            "EXPECTED_RELEASES": json.dumps([release]), "CHART_ONLY_RELEASES": "[]",
            "KUBECONFIG_DEV": "synthetic-fixture-config",
            "DEV_HELM_VALUES_GZIP_BASE64": base64.b64encode(gzip.compress(json.dumps(values).encode())).decode(),
        }
        with patch.dict(os.environ, environment), patch.object(deploy, "read_promotions", return_value={release: "sha256:" + "a" * 64}), patch.object(deploy, "command") as helm:
            with self.assertRaisesRegex(ValueError, "cannot disable NetworkPolicy"):
                deploy.main()
            helm.assert_not_called()

    def test_environment_inputs_precede_private_ci_values_and_digest(self):
        root = Path(__file__).resolve().parents[2]
        arguments = deploy.environment_value_arguments("tenantwright-gateway", root)
        self.assertTrue(all("environments/dev" in value.replace("\\", "/") for value in arguments[1::2]))
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(ValueError, "Missing tracked dev"):
                deploy.environment_value_arguments("tenantwright-auth", Path(folder))

    def test_chart_only_digest_is_immutable_and_repository_bound(self):
        digest = "sha256:" + "a" * 64
        self.assertEqual(deploy.deployed_image_digest({"image": {"repository": "yash9157/tenantwright-identity", "digest": digest}}, "yash9157/tenantwright-identity"), digest)
        for image in [{"repository": "yash9157/tenantwright-identity", "tag": "latest"}, {"repository": "other/image", "digest": digest}]:
            with self.assertRaises(ValueError):
                deploy.deployed_image_digest({"image": image}, "yash9157/tenantwright-identity")

    def test_chart_only_accepts_no_image_artifacts_but_rejects_extras(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(deploy.read_promotions(folder, []), {})
            (Path(folder) / "extra.yaml").write_text("images:\\n  tenantwright-auth: sha256:".replace("\\\\n", "\\n") + "a" * 64 + "\\n")
            with self.assertRaises(ValueError):
                deploy.read_promotions(folder, [])

    def test_failure_category_does_not_expose_captured_secrets(self):
        import subprocess
        failure = subprocess.CompletedProcess([], 1, "secret-value", "context deadline exceeded: private-value")
        with patch.object(deploy.subprocess, "run", return_value=failure):
            with self.assertRaisesRegex(RuntimeError, "timeout waiting") as caught:
                deploy.command(["helm", "upgrade"], {})
        self.assertNotIn("secret-value", str(caught.exception))
        self.assertNotIn("private-value", str(caught.exception))

    def test_core_releases_remain_ordered_and_batches_are_bounded(self):
        names = ["tenantwright-notifications", *reversed(deploy.CORE_RELEASES),
                 "tenantwright-auditlogs", "tenantwright-workflows"]
        plans = [(name,) for name in names]
        batches = list(deploy.deployment_batches(plans))
        self.assertEqual([batch[0][0] for batch in batches[:5]], list(deploy.CORE_RELEASES))
        self.assertEqual([len(batch) for batch in batches], [1, 1, 1, 1, 1, 3])

    def test_three_workers_complete_before_next_batch(self):
        rendezvous = threading.Barrier(3, timeout=5)
        completed = set()
        lock = threading.Lock()
        def rollout(plan):
            number = plan[0]
            if number < 3:
                rendezvous.wait()
            else:
                with lock:
                    self.assertTrue({0, 1, 2}.issubset(completed))
            with lock:
                completed.add(number)
        deploy.deploy_batches([(number,) for number in range(4)], rollout)
        self.assertEqual(completed, {0, 1, 2, 3})

    def test_failed_batch_drains_workers_and_never_starts_next_batch(self):
        rendezvous = threading.Barrier(3, timeout=5)
        completed = set()
        lock = threading.Lock()
        def rollout(plan):
            number = plan[0]
            self.assertLess(number, 3, "Next batch must not start")
            rendezvous.wait()
            if number == 0:
                raise RuntimeError("simulated failure")
            with lock:
                completed.add(number)
        with self.assertRaisesRegex(RuntimeError, "simulated failure"):
            deploy.deploy_batches([(number,) for number in range(6)], rollout)
        self.assertEqual(completed, {1, 2})

    def test_rejects_missing_and_invalid_artifacts(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError):
                deploy.read_promotions(folder, ["tenantwright-auth"])
            artifact = Path(folder) / "auth.yaml"
            artifact.write_text("images:\n  tenantwright-auth: latest\n")
            with self.assertRaises(ValueError):
                deploy.read_promotions(folder, ["tenantwright-auth"])
            artifact.write_text("images:\n  tenantwright-auth: sha256:" + "a" * 64 + "\n")
            self.assertEqual(list(deploy.read_promotions(folder, ["tenantwright-auth"])), ["tenantwright-auth"])
            with self.assertRaises(ValueError):
                deploy.read_promotions(folder, ["tenantwright-auth", "tenantwright-tenant"])

    def test_failed_release_requests_rollback_and_stops_batch(self):
        releases = ["tenantwright-auth", "tenantwright-tenant"]
        calls = []
        def command(args, env):
            self.assertNotIn("KUBECONFIG_DEV", env)
            self.assertNotIn("DEV_HELM_VALUES_GZIP_BASE64", env)
            calls.append(args)
            if args[1] == "status":
                return json.dumps({"info": {"status": "deployed"}})
            if args[1] == "upgrade":
                raise RuntimeError("simulated rollout failure")
            return ""
        environment = {
            "EXPECTED_RELEASES": json.dumps(releases),
            "KUBECONFIG_DEV": "private-test-config",
            "DEV_HELM_VALUES_GZIP_BASE64": base64.b64encode(gzip.compress(json.dumps({r: {"image": {"repository": "yash9157/" + r}} for r in releases}).encode())).decode(),
        }
        with patch.dict(os.environ, environment), patch.object(deploy, "command", command), patch.object(deploy, "read_promotions", return_value={r: "sha256:"+"a"*64 for r in releases}), patch.object(Path, "is_file", return_value=True):
            with self.assertRaises(RuntimeError):
                deploy.main()
        upgrades = [c for c in calls if c[1] == "upgrade"]
        self.assertEqual(len(upgrades), 1)
        files = [upgrades[0][index + 1] for index, argument in enumerate(upgrades[0]) if argument == "-f"]
        self.assertTrue(files[-1].endswith("tenantwright-auth.json"))
        self.assertIn("common.values.yaml", files[0])
        self.assertIn("--atomic", upgrades[0])
        self.assertIn("--wait-for-jobs", upgrades[0])
        timeout_index = upgrades[0].index("--timeout")
        self.assertEqual(upgrades[0][timeout_index + 1], deploy.HELM_OPERATION_TIMEOUT)
        self.assertEqual(deploy.HELM_OPERATION_TIMEOUT, "5m")
        self.assertNotIn("--no-hooks", upgrades[0])
        self.assertNotIn("--install", upgrades[0])


if __name__ == "__main__":
    unittest.main()
