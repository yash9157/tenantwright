"""Deploy the exact artifacts produced by this run to existing dev Helm releases."""
import base64
from concurrent.futures import ThreadPoolExecutor, wait
import gzip
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

DIGEST = re.compile(r"images:\s*\n  (tenantwright-[a-z-]+): (sha256:[0-9a-f]{64})\s*")
NAME = re.compile(r"tenantwright-[a-z-]+")
DEPLOY_CONCURRENCY = 3
HELM_OPERATION_TIMEOUT = "5m"
CORE_RELEASES = (
    "tenantwright-auth", "tenantwright-gateway", "tenantwright-identity",
    "tenantwright-tenant", "tenantwright-permissions",
)


def deployment_batches(plans):
    for release in CORE_RELEASES:
        for plan in plans:
            if plan[0] == release:
                yield [plan]
    remaining = [plan for plan in plans if plan[0] not in CORE_RELEASES]
    for offset in range(0, len(remaining), DEPLOY_CONCURRENCY):
        yield remaining[offset:offset + DEPLOY_CONCURRENCY]


def deploy_batches(plans, deploy_release):
    # Submit only the current batch. Drain it before propagating any failure,
    # so Helm rollback and temporary kubeconfig lifetimes remain intact.
    with ThreadPoolExecutor(max_workers=DEPLOY_CONCURRENCY) as executor:
        for batch in deployment_batches(plans):
            futures = [executor.submit(deploy_release, plan) for plan in batch]
            wait(futures)
            for future in futures:
                future.result()


def read_promotions(directory, expected):
    promotions = {}
    for path in Path(directory).glob("*.yaml"):
        match = DIGEST.fullmatch(path.read_text())
        if not match:
            raise ValueError(f"Invalid digest artifact: {path.name}")
        release, digest = match.groups()
        if release in promotions:
            raise ValueError(f"Duplicate digest artifact: {release}")
        promotions[release] = digest
    if set(promotions) != set(expected):
        raise ValueError("Digest artifacts must match every expected release exactly")
    return promotions


def command(args, env):
    result = subprocess.run(args, env=env, capture_output=True, text=True)
    if result.returncode:
        # Helm errors can include rendered values. Never print captured output.
        output = (result.stderr + result.stdout).lower()
        categories = {
            "context deadline exceeded": "timeout waiting for hooks or workload readiness",
            "timed out waiting": "timeout waiting for hooks or workload readiness",
            "forbidden": "deployment identity lacks required Kubernetes permissions",
            "another operation": "another Helm operation is in progress",
            "failed pre-upgrade": "pre-upgrade hook failed",
            "failed post-upgrade": "post-upgrade hook failed",
        }
        category = next((label for marker, label in categories.items() if marker in output),
                        "unclassified Helm failure; inspect release history and pod/job status")
        raise RuntimeError(f"{args[0]} {args[1]} failed (exit {result.returncode}): {category}")
    return result.stdout


def deployed_image_digest(values, repository):
    image = values.get("image", {})
    digest = image.get("digest", "")
    if image.get("repository") != repository or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise ValueError("Chart-only deployment requires a matching immutable deployed image digest")
    return digest


def environment_value_arguments(release, root=Path(".")):
    environment = root / "charts/environments/dev"
    component = release.removeprefix("tenantwright-")
    files = [] if component == "gateway" else [
        environment / "backend/common.values.yaml",
        root / "charts/tenantwright-common/files/messaging.values.yaml",
        environment / "backend/messaging.values.yaml",
    ]
    files.append(environment / f"backend/{component}.values.yaml")
    for file in files:
        if not file.is_file():
            raise ValueError(f"Missing tracked dev environment values for {release}")
    return [argument for file in files for argument in ("-f", str(file))]


def validate_backend_environment(values):
    policy = values.get("networkPolicy", {})
    if policy.get("enabled") is False or policy.get("ingressController", {}).get("enabled") is False:
        raise ValueError("Backend environment values cannot disable NetworkPolicy or its ingress controller policy")


def main():
    expected = json.loads(os.environ["EXPECTED_RELEASES"])
    if not isinstance(expected, list) or not all(isinstance(n, str) and NAME.fullmatch(n) for n in expected):
        raise ValueError("Invalid release list")
    if len(set(expected)) != len(expected):
        raise ValueError("Duplicate expected release")
    chart_only = json.loads(os.environ.get("CHART_ONLY_RELEASES", "[]"))
    if not isinstance(chart_only, list) or not all(isinstance(n, str) for n in chart_only):
        raise ValueError("Invalid chart-only release list")
    if len(set(chart_only)) != len(chart_only) or not set(chart_only) <= set(expected):
        raise ValueError("Chart-only releases must be a unique subset of expected releases")
    if not expected:
        raise ValueError("No releases selected")
    promotions = read_promotions("image-digests", [name for name in expected if name not in chart_only])
    kubeconfig = os.environ.get("KUBECONFIG_DEV", "")
    encoded_values = os.environ.get("DEV_HELM_VALUES_GZIP_BASE64", "").strip()
    if not encoded_values:
        raise ValueError("Configure DEV_HELM_VALUES_GZIP_BASE64 in the dev environment")
    overlays = json.loads(gzip.decompress(base64.b64decode(encoded_values, validate=True)))
    if not kubeconfig or not isinstance(overlays, dict):
        raise ValueError("Configure KUBECONFIG_DEV and valid environment values in the dev environment")
    plans = []
    for release in expected:
        chart = Path(f"charts/{release}")
        if not chart.joinpath("Chart.yaml").is_file():
            raise ValueError(f"Missing chart for {release}")
        values = overlays.get(release)
        if not isinstance(values, dict) or not values:
            raise ValueError(f"Missing complete reviewed environment values for {release}")
        repository = values.get("image", {}).get("repository")
        if repository != f"yash9157/{release}":
            raise ValueError(f"Unexpected image repository for {release}")
        validate_backend_environment(values)
        plans.append((release, chart, values, promotions.get(release)))
    environment_arguments = {release: environment_value_arguments(release) for release, _, _, _ in plans}
    env = os.environ.copy()
    env.pop("KUBECONFIG_DEV", None)
    env.pop("DEV_HELM_VALUES_GZIP_BASE64", None)
    with tempfile.TemporaryDirectory(prefix="tenantwright-deploy-") as directory:
        root = Path(directory)
        config = root / "kubeconfig"
        config.write_text(kubeconfig)
        config.chmod(0o600)
        env["KUBECONFIG"] = str(config)
        # Preflight the complete batch before modifying any release.
        for index, (release, chart, values, digest) in enumerate(plans):
            status = json.loads(command(["helm", "status", release, "-n", "tenantwright", "-o", "json"], env))
            if status["info"]["status"] != "deployed":
                raise ValueError(f"{release} is not a healthy deployed Helm release")
            if digest is None:
                deployed = json.loads(command(["helm", "get", "values", release, "-n", "tenantwright", "-a", "-o", "json"], env))
                digest = deployed_image_digest(deployed, values["image"]["repository"])
                plans[index] = (release, chart, values, digest)
                print(f"Chart-only: {release}; retaining deployed image {digest}", flush=True)
            command(["helm", "dependency", "build", str(chart)], env)
            value_file = root / f"{release}.json"
            value_file.write_text(json.dumps(values))
            value_file.chmod(0o600)
            command(["helm", "lint", str(chart), *environment_arguments[release], "-f", str(value_file), "--set-string", f"image.digest={digest}"], env)
            command(["helm", "template", release, str(chart), "-n", "tenantwright", *environment_arguments[release], "-f", str(value_file), "--set-string", f"image.digest={digest}"], env)
        def deploy_release(plan):
            release, chart, values, digest = plan
            print(f"Deploying {release} at {digest}", flush=True)
            try:
                command([
                    "helm", "upgrade", release, str(chart), "-n", "tenantwright",
                    "--reset-values", *environment_arguments[release], "-f", str(root / f"{release}.json"),
                    "--set-string", f"image.digest={digest}",
                    "--atomic", "--wait", "--wait-for-jobs",
                    "--timeout", HELM_OPERATION_TIMEOUT,
                    "--history-max", "10",
                ], env)
            except RuntimeError:
                print(f"FAILED: {release}. Helm atomic rollback was requested; inspect Helm status if rollback also failed. Database changes are not reversed.", flush=True)
                raise
            print(f"Ready: {release}", flush=True)

        deploy_batches(plans, deploy_release)


if __name__ == "__main__":
    main()
