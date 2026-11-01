"""Deterministic CI migration-lineage regression tests."""

import subprocess
import sys
from pathlib import Path
import tempfile
import unittest


sys.path.insert(0, str(Path(__file__).parent))
from verify_migration_lineage import MIGRATION_ARTIFACT, lineage_violations, migration_artifacts


BASE = "services/Identity/Identity.EntityFrameworkCore/Migrations/20260726071923_Initial.cs"
DESIGNER = BASE.replace(".cs", ".Designer.cs")
NEW = "services/Identity/Identity.EntityFrameworkCore/Migrations/20260915091000_AddSessionAudit.cs"


class LineageTests(unittest.TestCase):
    def test_existing_migration_and_designer_are_immutable(self):
        base = {BASE: "a", DESIGNER: "b"}
        self.assertEqual(lineage_violations(base, {BASE: "changed", DESIGNER: "b"}),
                         [f"Modified existing migration artifact: {BASE}"])

    def test_removal_or_rename_is_rejected(self):
        self.assertEqual(lineage_violations({BASE: "a"}, {NEW: "new"}),
                         [f"Removed or renamed existing migration artifact: {BASE}"])

    def test_copy_of_existing_artifact_is_rejected(self):
        self.assertEqual(lineage_violations({BASE: "a"}, {BASE: "a", NEW: "a"}),
                         [f"Copied existing migration artifact to a new path: {NEW}"])

    def test_new_migration_and_snapshot_change_are_allowed(self):
        snapshot = BASE.replace("20260726071923_Initial.cs", "IdentityDbContextModelSnapshot.cs")
        self.assertEqual(lineage_violations({BASE: "a"}, {BASE: "a", NEW: "new", snapshot: "changed"}), [])
        self.assertIsNone(MIGRATION_ARTIFACT.fullmatch(snapshot))
        self.assertIsNotNone(MIGRATION_ARTIFACT.fullmatch(NEW.replace(".cs", ".Designer.cs")))

    def test_git_tree_comparison_reads_real_blob_ids(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            path = root / BASE
            path.parent.mkdir(parents=True)
            path.write_text("initial", encoding="utf-8")
            subprocess.run(["git", "-C", str(root), "add", BASE], check=True)
            subprocess.run(["git", "-C", str(root), "-c", "user.name=CI", "-c", "user.email=ci@example.invalid",
                            "commit", "-q", "-m", "base"], check=True)
            base = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
            path.write_text("changed", encoding="utf-8")
            subprocess.run(["git", "-C", str(root), "add", BASE], check=True)
            subprocess.run(["git", "-C", str(root), "-c", "user.name=CI", "-c", "user.email=ci@example.invalid",
                            "commit", "-q", "-m", "head"], check=True)
            head = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
            prior = Path.cwd()
            try:
                import os
                os.chdir(root)
                self.assertEqual(lineage_violations(migration_artifacts(base), migration_artifacts(head)),
                                 [f"Modified existing migration artifact: {BASE}"])
            finally:
                os.chdir(prior)


if __name__ == "__main__":
    unittest.main()
