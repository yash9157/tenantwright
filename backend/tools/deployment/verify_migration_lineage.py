"""Reject changes to migration artifacts already present at the event baseline."""

import argparse
import re
import subprocess
import sys


MIGRATION_ARTIFACT = re.compile(r"^(?:app|services)/.+/Migrations/(\d{14}_[^/]+?)(?:\.Designer)?\.cs$")


def migration_artifacts(revision):
    result = subprocess.run(
        ["git", "ls-tree", "-r", "-z", "--full-tree", revision, "--", "app", "services"],
        check=True,
        capture_output=True,
    )
    artifacts = {}
    for entry in result.stdout.split(b"\0"):
        if not entry:
            continue
        metadata, path = entry.split(b"\t", 1)
        name = path.decode("utf-8")
        if MIGRATION_ARTIFACT.fullmatch(name):
            artifacts[name] = metadata.split()[2].decode("ascii")
    return artifacts


def lineage_violations(base_artifacts, head_artifacts):
    violations = []
    for path, blob in sorted(base_artifacts.items()):
        if path not in head_artifacts:
            violations.append(f"Removed or renamed existing migration artifact: {path}")
        elif blob != head_artifacts[path]:
            violations.append(f"Modified existing migration artifact: {path}")

    existing_blobs = set(base_artifacts.values())
    for path, blob in sorted(head_artifacts.items()):
        if path not in base_artifacts and blob in existing_blobs:
            violations.append(f"Copied existing migration artifact to a new path: {path}")
    return violations


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="PR base or push before SHA")
    parser.add_argument("--head", required=True, help="current checkout SHA")
    args = parser.parse_args()

    try:
        baseline = migration_artifacts(args.base)
        current = migration_artifacts(args.head)
    except subprocess.CalledProcessError as error:
        parser.exit(2, f"Cannot inspect migration baseline/head: {error}\n")

    violations = lineage_violations(baseline, current)
    for violation in violations:
        print(violation, file=sys.stderr)
    if violations:
        parser.exit(1, "Existing migrations are immutable; append a new migration instead.\n")
    print(f"Migration lineage intact: {len(baseline)} existing artifacts unchanged.")


if __name__ == "__main__":
    main()
