"""Select image and chart owners without treating CI maintenance as application changes."""
import json
import os
from http.client import IncompleteRead
from pathlib import Path
import re
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


IMAGE_JOB = re.compile(r"^Docker Build / Publish \(([a-z-]+)\)$")
IMAGE_ARTIFACT = re.compile(r"^tenantwright-image-digest-([a-z-]+)$")
ARTIFACT_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")


def github_json(repository, endpoint, token):
    request = Request(
        f"https://api.github.com/repos/{repository}/{endpoint}",
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    for attempt in range(3):
        try:
            with urlopen(request, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code not in {429, 500, 502, 503, 504} or attempt == 2:
                raise
        except (URLError, IncompleteRead, TimeoutError, OSError, json.JSONDecodeError):
            if attempt == 2:
                raise
        time.sleep(2 ** attempt)
    raise RuntimeError("GitHub API retry loop terminated unexpectedly")


def github_pages(repository, endpoint, key, token):
    received = 0
    for page in range(1, 11):
        separator = "&" if "?" in endpoint else "?"
        response = github_json(repository, f"{endpoint}{separator}per_page=100&page={page}", token)
        items = response.get(key, [])
        received += len(items)
        yield items
        if received >= response.get("total_count", received):
            return
    raise ValueError(f"GitHub API pagination incomplete for {key}")


def github_items(repository, endpoint, key, token):
    return [item for page in github_pages(repository, endpoint, key, token) for item in page]


def successful_base(repository, branch, current_run, workflow):
    token = os.environ.get("GH_TOKEN")
    if not token:
        return None

    query = urlencode({"branch": branch, "event": "push", "status": "success", "per_page": 100})
    try:
        runs = github_json(repository, f"actions/workflows/{workflow}/runs?{query}", token).get("workflow_runs", [])
    except (HTTPError, URLError, IncompleteRead, TimeoutError, OSError, ValueError):
        # A missing/unavailable baseline is safe: the caller falls back to all
        # services instead of risking an incomplete affected-image selection.
        return None

    return next((run["head_sha"] for run in runs
                 if str(run.get("id")) != str(current_run) and run.get("head_sha")), None)


def published_image_checkpoints(repository, branch, current_run, workflow, current_sha, service_names):
    """Find each service's newest trusted, unexpired image digest artifact.

    A failed deployment does not disqualify its successful Docker image jobs.
    Services without successful jobs and matching artifacts remain uncovered
    and are rebuilt by delivery_plan rather than silently reusing an old image.
    """
    token = os.environ.get("GH_TOKEN")
    if not token:
        return {}
    checkpoints = {}
    remaining = set(service_names)
    query = urlencode({"branch": branch, "event": "push"})
    try:
        for runs in github_pages(repository, f"actions/workflows/{workflow}/runs?{query}",
                                 "workflow_runs", token):
            for run in runs:
                if not remaining:
                    return checkpoints
                run_id = run.get("id")
                head_sha = run.get("head_sha")
                if not run_id or not head_sha or str(run_id) == str(current_run) or run.get("status") != "completed":
                    continue
                if subprocess.run(["git", "merge-base", "--is-ancestor", head_sha, current_sha],
                                  capture_output=True).returncode != 0:
                    continue
                jobs = github_items(repository, f"actions/runs/{run_id}/jobs", "jobs", token)
                successful_jobs = {match.group(1) for job in jobs
                                   if job.get("conclusion") == "success"
                                   and (match := IMAGE_JOB.fullmatch(job.get("name", "")))
                                   and match.group(1) in remaining}
                if not successful_jobs:
                    continue
                artifacts = github_items(repository, f"actions/runs/{run_id}/artifacts", "artifacts", token)
                available = set()
                duplicates = set()
                for artifact in artifacts:
                    match = IMAGE_ARTIFACT.fullmatch(artifact.get("name", ""))
                    service = match.group(1) if match else None
                    origin = artifact.get("workflow_run") or {}
                    if (service not in successful_jobs or artifact.get("expired")
                            or not ARTIFACT_DIGEST.fullmatch(artifact.get("digest") or "")
                            or str(origin.get("id")) != str(run_id)
                            or origin.get("head_sha") != head_sha):
                        continue
                    if service in available:
                        duplicates.add(service)
                    available.add(service)
                available.difference_update(duplicates)
                for service in available & remaining:
                    checkpoints[service] = {"head_sha": head_sha, "run_id": str(run_id)}
                    remaining.remove(service)
    except (HTTPError, URLError, IncompleteRead, TimeoutError, OSError, ValueError):
        # Previously verified checkpoints remain trustworthy. Any service not
        # covered before the failure is rebuilt instead of reusing unknown data.
        return checkpoints
    return checkpoints


def changed_services_since_checkpoints(checkpoints, current_sha, services, prefixes, root=Path('.')):
    """Return checkpointed services whose dependency closure changed afterwards."""
    changed = set()
    services_by_sha = {}
    for service, checkpoint in checkpoints.items():
        services_by_sha.setdefault(checkpoint["head_sha"], set()).add(service)
    for head_sha, checkpoint_services in services_by_sha.items():
        files = subprocess.check_output(
            ["git", "diff", "--name-only", head_sha, current_sha],
            cwd=root, text=True).splitlines()
        affected, _ = select_changes(files, services, prefixes, root)
        changed.update(checkpoint_services & set(affected))
    return changed


def reuse_run_groups(checkpoints, reused_services, service_order):
    """Group reused services by the exact workflow run holding their artifacts."""
    reused = set(reused_services)
    grouped = {}
    for service in service_order:
        if service not in reused:
            continue
        run_id = checkpoints[service]["run_id"]
        grouped.setdefault(run_id, []).append(service)
    return [{"run_id": run_id, "services": ",".join(services)}
            for run_id, services in grouped.items()]


def delivery_plan(service_order, pending_images, changed_since_image, available_artifacts, chart_only):
    """Return new builds, pending releases, prior-image reuse, and chart-only releases."""
    pending = set(pending_images)
    builds = pending & (set(changed_since_image) | (pending - set(available_artifacts)))
    reused = pending - builds
    charts = set(chart_only) - pending
    return (
        [name for name in service_order if name in builds],
        [name for name in service_order if name in pending | charts],
        [name for name in service_order if name in reused],
        [name for name in service_order if name in charts],
    )


def select_changes(files, services, prefixes, root=Path('.')):
    names = [service['service'] for service in services]
    images, charts = set(), set()
    source_files = []
    global_inputs = {'TenantWright.slnx', '.dockerignore', 'Directory.Build.props',
                     'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config',
                     'nuget.config', 'NuGet.config', 'global.json'}
    for file in files:
        if file.startswith('charts/'):
            if file.startswith('charts/environments/dev/') and '/private/' not in file:
                component = Path(file).name.removesuffix('.values.yaml')
                if file.startswith('charts/environments/dev/backend/') and component in names:
                    charts.add(component)
                elif file in {
                    'charts/environments/dev/backend/common.values.yaml',
                    'charts/environments/dev/backend/messaging.values.yaml',
                    'charts/environments/dev/images.lock.yaml',
                }:
                    charts.update(names)
            for name in names:
                if file.startswith(f'charts/tenantwright-{name}/'):
                    charts.add(name)
            # Shared library charts are dependencies of all application charts.
            if file.startswith('charts/tenantwright-common/'):
                charts.update(names)
        elif file in global_inputs:
            images.update(names)
        elif file.startswith(('shared/', 'services/', 'app/', 'gateway/')):
            source_files.append(file)
            images.update(name for name in names if any(file.startswith(p) for p in prefixes[name]))

    # Walk project references transitively, including cross-service contracts.
    projects = [Path(p) for p in subprocess.check_output(
        ['git', 'ls-files', '*.csproj'], cwd=root, text=True).splitlines()]
    references = {}
    changed_projects = set()
    for project in projects:
        absolute = (root / project).resolve()
        directory = project.parent.as_posix() + '/'
        if any(file.startswith(directory) for file in source_files):
            changed_projects.add(absolute)
        if absolute.exists():
            references[absolute] = {(absolute.parent / item.attrib['Include'].replace('\\', '/')).resolve()
                                    for item in ET.parse(absolute).iter('ProjectReference')
                                    if 'Include' in item.attrib}
    while True:
        consumers = {project for project, refs in references.items() if refs & changed_projects}
        if consumers <= changed_projects:
            break
        changed_projects.update(consumers)
    for project in changed_projects:
        relative = project.relative_to(root.resolve()).as_posix()
        images.update(name for name in names if any(relative.startswith(p) for p in prefixes[name]))
    # Removed/unmapped shared build files cannot be resolved in the current graph.
    if any(file.startswith('shared/') and not any(file.startswith(p.parent.as_posix() + '/') for p in projects)
           for file in source_files):
        images.update(names)
    return [name for name in names if name in images], [name for name in names if name in charts - images]
