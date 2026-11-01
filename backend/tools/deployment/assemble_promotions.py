"""Assemble exact immutable promotions from current and certified prior runs."""

import os
from pathlib import Path
import re
import sys


DIGEST_ARTIFACT = re.compile(r"images:\s*\n  (tenantwright-[a-z-]+): (sha256:[0-9a-f]{64})\s*")
ARTIFACT_FILE = re.compile(r"tenantwright-[a-z-]+\.yaml")


def read_artifacts(directory):
    promotions = {}
    for path in Path(directory).glob("*.yaml"):
        if not ARTIFACT_FILE.fullmatch(path.name):
            raise ValueError(f"Unexpected digest artifact filename: {path.name}")
        match = DIGEST_ARTIFACT.fullmatch(path.read_text(encoding="utf-8"))
        if not match or path.stem != match.group(1):
            raise ValueError(f"Invalid digest artifact: {path.name}")
        release, digest = match.groups()
        if release in promotions:
            raise ValueError(f"Duplicate digest artifact: {release}")
        promotions[release] = digest
    return promotions


def assemble(current_directory, prior_directory, output_directory, new_services, reused_services):
    current = read_artifacts(current_directory)
    prior = read_artifacts(prior_directory)
    new_releases = {f"tenantwright-{service}" for service in new_services}
    reused_releases = {f"tenantwright-{service}" for service in reused_services}
    if new_releases & reused_releases or set(current) != new_releases or not reused_releases <= set(prior):
        raise ValueError("Current/prior digest artifacts do not match the selected release sources")

    output = Path(output_directory)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Promotion output directory must start empty")
    output.mkdir(parents=True, exist_ok=True)
    for release, digest in sorted({**{r: prior[r] for r in reused_releases}, **current}.items()):
        (output / f"{release}.yaml").write_text(f"images:\n  {release}: {digest}\n", encoding="utf-8")
    return sorted(new_releases | reused_releases)


def filter_reused(source_directory, output_directory, services):
    source = read_artifacts(source_directory)
    expected = {f"tenantwright-{service}" for service in services}
    if not expected or not expected <= set(source):
        raise ValueError("Downloaded digest artifacts do not match the declared reused services")

    output = Path(output_directory)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Filtered digest output directory must start empty")
    output.mkdir(parents=True, exist_ok=True)
    for release in sorted(expected):
        (output / f"{release}.yaml").write_text(
            f"images:\n  {release}: {source[release]}\n", encoding="utf-8")
    return sorted(expected)


def main():
    if sys.argv[1:] == ["filter-reused"]:
        services = [service for service in os.environ.get("EXPECTED_REUSED_SERVICES", "").split(",")
                    if service]
        releases = filter_reused("downloaded-reused-image-digests",
                                 "filtered-reused-image-digests", services)
        print(f"Verified {len(releases)} reused image digest artifacts.")
        return
    if sys.argv[1:]:
        raise SystemExit("Usage: assemble_promotions.py [filter-reused]")
    new_services = [service for service in os.environ.get("NEW_IMAGE_SERVICES", "").split(",") if service]
    reused_services = [service for service in os.environ.get("REUSED_IMAGE_SERVICES", "").split(",") if service]
    releases = assemble("current-image-digests", "reused-image-digests", "image-digests",
                        new_services, reused_services)
    print(f"Verified exact immutable promotion digests for {len(releases)} releases.")


if __name__ == "__main__":
    main()
