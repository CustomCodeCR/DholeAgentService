#!/usr/bin/env python3
"""Dispatch existing protected staging deploy + circuit activation after a verified backup.

The actual deploy and activation remain owned by their existing guarded workflows.
No database, browser or provider calls occur here.
"""
import datetime as dt
import json
import os
import time
import urllib.error
import urllib.request

repository = os.environ["GITHUB_REPOSITORY"]
token = os.environ["GH_TOKEN"]
backup_dir = os.environ["PHASE2_BACKUP_DIR"]
base = "https://api.github.com/repos/" + repository
headers = {
    "Authorization": "Bearer " + token,
    "Accept": "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
}


def request(method, endpoint, data=None):
    body = None if data is None else json.dumps(data).encode("utf-8")
    req = urllib.request.Request(base + endpoint, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            content = response.read()
            return json.loads(content) if content else {}
    except urllib.error.HTTPError as exc:
        # Never print request headers, tokens or payloads.
        raise RuntimeError(f"GitHub protected workflow request failed: HTTP {exc.code}") from None


def runs(workflow):
    data = request(
        "GET", f"/actions/workflows/{workflow}/runs?branch=develop&event=workflow_dispatch&per_page=25"
    )
    return data.get("workflow_runs", [])


def invoke_and_verify(workflow, inputs, expected_sha):
    before = max([r["id"] for r in runs(workflow)] + [0])
    print(f"DISPATCH_PROTECTED_WORKFLOW={workflow}", flush=True)
    request("POST", f"/actions/workflows/{workflow}/dispatches", {"ref": "develop", "inputs": inputs})
    chosen = None
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        matches = [
            r for r in runs(workflow)
            if r["id"] > before and r.get("head_branch") == "develop"
            and r.get("head_sha") == expected_sha
            and r.get("event") == "workflow_dispatch"
        ]
        if matches:
            chosen = min(matches, key=lambda r: r["id"])
            break
        time.sleep(8)
    if chosen is None:
        raise RuntimeError("Protected workflow dispatched but no matching develop SHA run was located")

    run_id = chosen["id"]
    print(f"PROTECTED_WORKFLOW_RUN={workflow}#{run_id}", flush=True)
    deadline = time.monotonic() + 2700
    while time.monotonic() < deadline:
        details = request("GET", f"/actions/runs/{run_id}")
        if details.get("status") == "completed":
            result = details.get("conclusion")
            print(f"PROTECTED_WORKFLOW_RESULT={workflow}:{result}", flush=True)
            if result != "success":
                raise RuntimeError(f"Protected workflow {workflow} failed; no further activation")
            return run_id
        time.sleep(20)
    raise RuntimeError(f"Protected workflow {workflow} did not complete inside safety deadline")


if __name__ == "__main__":
    if not backup_dir.startswith("/home/dhole-agent-phase2-backups/staging/"):
        raise SystemExit("Backup path outside the guarded staging root")
    sha = request("GET", "/branches/develop")["commit"]["sha"]
    if len(sha) != 40:
        raise SystemExit("Invalid develop release SHA")
    print("EXPECTED_PROTECTED_STAGING_SHA=" + sha, flush=True)
    invoke_and_verify(
        "deploy-staging.yml",
        {"confirmation": "DEPLOY_AGENT_STAGING", "backup_dir": backup_dir},
        sha,
    )
    current = request("GET", "/branches/develop")["commit"]["sha"]
    if current != sha:
        raise SystemExit("Develop SHA changed during deployment; activation withheld")
    invoke_and_verify(
        "maersk-phase2-activate.yml",
        {
            "environment": "staging",
            "confirmation": "ACTIVATE_MAERSK_CIRCUIT_STAGING",
            "backup_dir": backup_dir,
            "expected_release_sha": sha,
        },
        sha,
    )
    print("PHASE2_STAGING_PROTECTED_ACTIVATION_WORKFLOWS_SUCCEEDED=true", flush=True)
