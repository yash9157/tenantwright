"""Promotion assembly rejects missing, corrupt, extra, and mismatched artifacts."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("assemble_promotions", Path(__file__).with_name("assemble_promotions.py"))
promotions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(promotions)


def artifact(directory, release, digest=None):
    digest = digest or "sha256:" + "a" * 64
    (directory / f"{release}.yaml").write_text(f"images:\n  {release}: {digest}\n", encoding="utf-8")


class PromotionTests(unittest.TestCase):
    def directories(self, folder):
        root = Path(folder)
        current, prior, output = (root / name for name in ("current", "prior", "output"))
        current.mkdir()
        prior.mkdir()
        return current, prior, output

    def test_reuses_prior_digest_without_new_build(self):
        with tempfile.TemporaryDirectory() as folder:
            current, prior, output = self.directories(folder)
            artifact(prior, "tenantwright-auth")
            artifact(prior, "tenantwright-identity")
            self.assertEqual(promotions.assemble(current, prior, output, [], ["auth"]), ["tenantwright-auth"])
            self.assertEqual(promotions.read_artifacts(output), {"tenantwright-auth": "sha256:" + "a" * 64})

    def test_mixed_new_and_reused_promotions_are_exact(self):
        with tempfile.TemporaryDirectory() as folder:
            current, prior, output = self.directories(folder)
            artifact(current, "tenantwright-auth", "sha256:" + "b" * 64)
            artifact(prior, "tenantwright-identity")
            self.assertEqual(promotions.assemble(current, prior, output, ["auth"], ["identity"]),
                             ["tenantwright-auth", "tenantwright-identity"])
            self.assertEqual(promotions.read_artifacts(output)["tenantwright-auth"], "sha256:" + "b" * 64)

    def test_missing_prior_artifact_fails_before_output(self):
        with tempfile.TemporaryDirectory() as folder:
            current, prior, output = self.directories(folder)
            with self.assertRaisesRegex(ValueError, "do not match"):
                promotions.assemble(current, prior, output, [], ["auth"])
            self.assertFalse(output.exists())

    def test_extra_current_artifact_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            current, prior, output = self.directories(folder)
            artifact(current, "tenantwright-auth")
            with self.assertRaisesRegex(ValueError, "do not match"):
                promotions.assemble(current, prior, output, [], [])

    def test_corrupt_digest_or_mismatched_filename_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            current, prior, output = self.directories(folder)
            artifact(prior, "tenantwright-auth", "latest")
            with self.assertRaisesRegex(ValueError, "Invalid digest"):
                promotions.assemble(current, prior, output, [], ["auth"])
            (prior / "tenantwright-auth.yaml").write_text("images:\n  tenantwright-identity: sha256:" + "a" * 64 + "\n")
            with self.assertRaisesRegex(ValueError, "Invalid digest"):
                promotions.assemble(current, prior, output, [], ["auth"])

    def test_output_must_start_empty(self):
        with tempfile.TemporaryDirectory() as folder:
            current, prior, output = self.directories(folder)
            artifact(prior, "tenantwright-auth")
            output.mkdir()
            artifact(output, "tenantwright-tenant")
            with self.assertRaisesRegex(ValueError, "start empty"):
                promotions.assemble(current, prior, output, [], ["auth"])

    def test_filters_only_the_declared_reused_services(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, output = root / "source", root / "output"
            source.mkdir()
            artifact(source, "tenantwright-auth")
            artifact(source, "tenantwright-identity", "sha256:" + "b" * 64)

            self.assertEqual(promotions.filter_reused(source, output, ["auth"]), ["tenantwright-auth"])
            self.assertEqual(promotions.read_artifacts(output),
                             {"tenantwright-auth": "sha256:" + "a" * 64})

    def test_reused_filter_rejects_a_missing_declared_service(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, output = root / "source", root / "output"
            source.mkdir()

            with self.assertRaisesRegex(ValueError, "do not match"):
                promotions.filter_reused(source, output, ["auth"])
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
