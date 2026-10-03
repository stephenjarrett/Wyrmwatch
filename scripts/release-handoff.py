"""Trusted master orchestration: discover verified source and package checks.

AutomaticRelease coordinates immutable patch versions/tags around this policy.
Internal exact-run callbacks need no manual input, CI reruns or downloaded code execution.
"""
import base64
import importlib.util
import json
import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("manage_release", ROOT / "scripts/manage-release.py")
policy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(policy)


class Handoff:
    def __init__(self, manager):
        self.manager = manager
        self.api, self.root = manager.api, manager.root

    def trusted(self, run, manager, packages=False):
        return manager.trusted_run(run, packages)

    def discover(self, manager, packages=False):
        matches = [run for run in self.runs(manager, packages)
                   if run.get("status") == "completed"]
        # Reruns have the same run ID and latest attempt; distinct completed push
        # runs are ambiguous, even if one passed. Never select by arbitrary order.
        policy.require(len(matches) <= 1, "Multiple matching push runs; publication is blocked")
        if not matches:
            return None
        run = matches[0]
        if not packages and run.get("conclusion") != "success":
            return None
        manager.checked_run(run["id"], packages=packages)
        if packages:
            jobs = self.api.api(f"{self.root}/actions/runs/{run['id']}/jobs?per_page=100")["jobs"]
            policy.require(run.get("conclusion") in {"success", "failure"}, "Tag run did not complete normally")
            # Recover the legacy publisher failure only; failed package checks
            # or any unrelated failed guard remain blocking.
            policy.require(all(job["name"] in policy.PACKAGES or job.get("conclusion") in {"success", "skipped"}
                               or (job["name"] == "publish" and job.get("conclusion") == "failure") for job in jobs),
                           "An additional tag-run guard failed; publication is blocked")
            if run["conclusion"] == "failure":
                policy.require(any(job["name"] == "publish" and job.get("conclusion") == "failure" for job in jobs),
                               "Only a legacy publisher failure can be recovered automatically")
        return int(run["id"])

    def runs(self, manager, packages=False):
        runs = {}
        queries = [(manager.tag, manager.head), ("master", manager.control_head)] if packages else [("master", manager.head)]
        for branch, head in queries:
            result = self.api.api(f"{self.root}/actions/runs?branch={branch}"
                                  f"&head_sha={head}&per_page=100")
            policy.require(result["total_count"] <= 100, "Run discovery requires pagination; refuse ambiguous results")
            for run in result["workflow_runs"]:
                if self.trusted(run, manager, packages):
                    runs[run["id"]] = run
        return list(runs.values())

    def await_callback(self, value, attempts=25, interval=5):
        # The notifying job is still running when its dispatch is accepted.
        # Wait only for that exact trusted run; no release writes or CI reruns.
        policy.require(re.fullmatch(r"[1-9][0-9]{0,19}", value or ""), "Invalid callback run ID")
        self.manager.ensure_release_allowed()
        run_id = int(value)
        for attempt in range(attempts):
            run = self.api.api(f"{self.root}/actions/runs/{run_id}")
            policy.require(run.get("id") == run_id
                           and (run.get("head_repository") or {}).get("full_name") == self.manager.repository
                           and run.get("event") in {"push", "workflow_dispatch"}
                           and run.get("head_branch") == "master"
                           and run.get("path") in {".github/workflows/build.yml", ".github/workflows/release.yml"}
                           and (run["path"] != ".github/workflows/release.yml" or run["event"] == "workflow_dispatch"),
                           "Callback must identify same-repository trusted master verification")
            if run.get("head_sha") != self.manager.head:
                return None
            self.manager.current_master()
            if run.get("status") == "completed":
                self.trigger(run_id)  # Reject failed source checks and abnormal package completions.
                return run_id
            policy.require(run.get("status") in {"queued", "in_progress", "pending", "waiting", "requested"}
                           and run.get("conclusion") is None, "Invalid callback verification state")
            policy.require(attempt + 1 < attempts, "Callback verification did not complete within two minutes")
            time.sleep(interval)
        raise policy.ReleaseError("Invalid callback wait configuration")

    def trigger(self, run_id):
        run = self.api.api(f"{self.root}/actions/runs/{int(run_id)}")
        policy.require(run.get("event") in {"push", "workflow_dispatch"} and run.get("status") == "completed"
                       and (run.get("head_repository") or {}).get("full_name") == self.manager.repository,
                       "Only completed same-repository verification events are trusted")
        if run.get("path") == ".github/workflows/build.yml":
            policy.require(run.get("head_branch") == "master" and run.get("conclusion") == "success",
                           "Master trigger must be a successful master push run")
            return run if run.get("head_sha") == self.manager.head else None
        policy.require(run.get("path") == ".github/workflows/release.yml"
                       and run.get("conclusion") in {"success", "failure"}
                       and ((run.get("event") == "push" and re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", run.get("head_branch", "")))
                            or (run.get("event") == "workflow_dispatch" and run.get("head_branch") == "master")),
                       "Tag trigger must be a version-tag package run")
        return run if (run["head_branch"] == self.manager.tag or
                       (run["event"] == "workflow_dispatch" and run["head_sha"] == self.manager.head)) else None

    def source_text(self, path, head):
        data = self.api.api(f"{self.root}/contents/{path}?ref={head}")
        policy.require(data.get("type") == "file" and data.get("encoding") == "base64"
                       and data.get("size", 0) <= 1024 * 1024, "Invalid or oversized pinned source metadata")
        encoded = "".join(data["content"].split())
        value = base64.b64decode(encoded, validate=True)
        policy.require(len(value) == data["size"], "Pinned source metadata size mismatch")
        return value.decode("utf-8-sig")

    def source_manager(self, head):
        props = ET.fromstring(self.source_text("Directory.Build.props", head))
        policy.require(props.findtext("PropertyGroup/Version") == self.manager.version,
                       "Approved tag belongs to a different project version")
        notes = self.source_text(f"docs/releases/{self.manager.tag}.md", head)
        source = policy.Manager(self.api, self.manager.repository, self.manager.version, head, notes,
                                control_head=self.manager.head)
        source.cover_control = getattr(self, "cover_control", False)
        return source

    def guard(self, verification_run):
        self.manager.ensure_release_allowed()
        self.manager.checked_run(verification_run)
        self.manager.current_master()
        releases = self.api.api(f"{self.root}/releases?per_page=100")
        policy.require(len(releases) < 100, "Release inventory requires pagination")
        version = tuple(map(int, self.manager.version.split(".")))
        for release in releases:
            match = re.fullmatch(r"v([0-9]+)\.([0-9]+)\.([0-9]+)", release.get("tag_name", ""))
            if match and not release.get("draft") and not release.get("prerelease"):
                policy.require(tuple(map(int, match.groups())) <= version,
                               "A newer version is published; refuse to supersede it")

    def run(self, event_run_id=None):
        self.manager.ensure_release_allowed()
        self.manager.current_master()
        trigger = self.trigger(event_run_id) if event_run_id is not None else None
        if event_run_id is not None and trigger is None:
            return "Stale or superseded event; no release changes"
        verification = self.discover(self.manager)
        if verification is None:
            return "Waiting for successful checks at current master; no release changes"
        self.guard(verification)
        refs = self.api.api(f"{self.root}/git/matching-refs/tags/{self.manager.tag}")
        exact = [ref for ref in refs if ref["ref"] == "refs/tags/" + self.manager.tag]
        policy.require(len(exact) <= 1, "Ambiguous version-tag approval")
        if not exact:
            result = self.manager.prepare(verification)
            return result + "; waiting for explicit " + self.manager.tag + " approval"

        approved = self.manager.tag_commit()
        if trigger and trigger["path"] == ".github/workflows/release.yml" and trigger["event"] == "push" and trigger["head_sha"] != approved:
            return "Stale approved-tag event; no release changes"
        source = self.source_manager(approved)
        candidate = source.find_release()
        metadata = policy.receipt(candidate.get("body"), source.repository, source.version) if candidate else None
        if metadata:
            policy.require(metadata["sha"] == source.head, "Prepared candidate differs from the approved tag")
            policy.validate_assets(candidate, metadata, complete=metadata["phase"] != "preparing")
            source.checked_run(metadata["run_id"])
            if not candidate["draft"]:
                policy.require(metadata["phase"] == "published", "Unknown published candidate")
                return "Matching release already published; no changes"
        if metadata is None or metadata["phase"] != "ready":
            source_run = int(metadata["run_id"]) if metadata else self.discover(source)
            if source_run is None:
                return "Waiting for successful checks at the approved tag source; no release changes"
            source.prepare(source_run, _source_guard=lambda: self.guard(verification))
        packages = self.discover(source, packages=True)
        if packages is None:
            return "Verified draft ready; waiting for both approved-tag package checks"
        return source.publish(packages, source.tag, _before_publish=lambda: self.guard(verification))


def main():
    event = os.environ.get("GITHUB_EVENT_NAME")
    policy.require(event in {"workflow_run", "workflow_dispatch", "schedule"}
                   and os.environ.get("GITHUB_REF") == "refs/heads/master",
                   "Automatic handoff executes only trusted default-branch orchestration")
    callback = os.environ.get("COMPLETED_RUN_ID", "")
    policy.require(not callback or (event == "workflow_dispatch"
                                   and re.fullmatch(r"[1-9][0-9]{0,19}", callback)),
                   "Callback run ID is valid only on trusted master dispatch")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    version = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/Version")
    manager = policy.Manager(policy.Gh(), os.environ["GITHUB_REPOSITORY"], version, head,
                             (ROOT / "docs/releases" / ("v" + version + ".md")).read_text(encoding="utf-8"))
    run_id = None
    if event == "workflow_run":
        event_file = Path(os.environ["GITHUB_EVENT_PATH"])
        policy.require(event_file.stat().st_size <= 1024 * 1024, "Oversized workflow event")
        payload = json.loads(event_file.read_text(encoding="utf-8"))
        policy.require(payload.get("repository", {}).get("full_name") == manager.repository,
                       "Workflow event repository differs")
        run_id = int(payload["workflow_run"]["id"])
    from automatic_release import AutomaticRelease
    coordinator = Handoff(manager)
    if callback:
        run_id = coordinator.await_callback(callback)
        result = (AutomaticRelease(coordinator, policy).run(run_id) if run_id is not None
                  else "Stale or superseded callback; no release changes")
    else:
        result = AutomaticRelease(coordinator, policy).run(run_id)
    print(result)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write("### Release handoff\n\n" + result + "\n")


if __name__ == "__main__":
    main()
