"""Prepare verified master artifacts as a draft; publish only an approved matching tag.

Uses the runner's normal gh session. Downloaded artifacts are data, never executed.
Both workflows share a non-cancelling concurrency group. Published releases are immutable.
"""
import argparse
import hashlib
import io
import json
import os
import re
import stat
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

MARKER = "wyrmwatch-release-receipt-v1"
CHECKS = {"build (windows-latest, win-x64)", "build (ubuntu-latest, linux-x64)"}
PACKAGES = {"package (windows-latest, win-x64)", "package (ubuntu-latest, linux-x64)"}
MAX_ASSET = 256 * 1024 * 1024
MAX_ARTIFACT = 512 * 1024 * 1024


class ReleaseError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise ReleaseError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def expected_files(version, runtime=None):
    names = {
        "win-x64": {"Wyrmwatch-win-x64.zip", f"Wyrmwatch-{version}-win-x64-setup.exe"},
        "linux-x64": {"Wyrmwatch-linux-x64.tar.gz", f"Wyrmwatch-{version}-linux-amd64.deb"},
    }
    files = names[runtime] if runtime else set.union(*names.values())
    return files | {name + ".sha256" for name in files}


def unpack_artifact(blob, digest, expected):
    require(len(blob) <= MAX_ARTIFACT, "Artifact exceeds the size bound")
    require(digest == "sha256:" + sha(blob), "Artifact archive digest mismatch or missing digest")
    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        entries = archive.infolist()
        require(len(entries) == len(expected) and {entry.filename for entry in entries} == expected,
                "Artifact contains missing, duplicate or unexpected paths")
        require(sum(entry.file_size for entry in entries) <= MAX_ARTIFACT, "Expanded artifact too large")
        require(all(entry.file_size <= MAX_ASSET and not stat.S_ISLNK(entry.external_attr >> 16)
                    for entry in entries), "Oversized or linked artifact entry")
        files = {entry.filename: archive.read(entry) for entry in entries}
    for name, data in files.items():
        if not name.endswith(".sha256"):
            checksum = files[name + ".sha256"].decode("utf-8").strip()
            require(re.fullmatch(re.escape(sha(data)) + r"[ \t]+\*?" + re.escape(name), checksum),
                    f"Package checksum mismatch: {name}")
    return files


def receipt(body, repository, version):
    matches = re.findall(r"<!-- " + MARKER + r" (.*?) -->", body or "")
    require(len(matches) == 1, "Draft is not owned by this release workflow")
    result = json.loads(matches[0])
    require(result.get("repository") == repository and result.get("version") == version,
            "Draft ownership/version does not match")
    require(re.fullmatch(r"[0-9a-f]{40}", result.get("sha", "")), "Invalid receipt source revision")
    require(result.get("phase") in {"preparing", "ready", "published"}, "Invalid draft phase")
    require(set(result.get("assets", {})) == expected_files(version), "Invalid receipt asset inventory")
    for inventory in (result["assets"], result.get("previous_assets", {})):
        require(set(inventory) <= expected_files(version) and all(re.fullmatch(r"[0-9a-f]{64}", value)
                for value in inventory.values()), "Invalid receipt digest inventory")
    return result


def validate_assets(release, metadata, complete=True):
    assets = release.get("assets", [])
    names = [asset["name"] for asset in assets]
    require(len(names) == len(set(names)), "Duplicate release asset")
    require(set(names) <= set(metadata["assets"]), "Unknown release asset; refuse to alter it")
    if complete:
        require(set(names) == set(metadata["assets"]), "Draft asset inventory is incomplete")
    for asset in assets:
        allowed = {metadata["assets"][asset["name"]]}
        if not complete:
            allowed.add(metadata.get("previous_assets", {}).get(asset["name"]))
        require(asset.get("digest") in {"sha256:" + value for value in allowed if value},
                f"Release asset changed or has no digest: {asset['name']}")


def release_body(notes, metadata):
    status = {"preparing": "Asset preparation is incomplete; publication is blocked.",
              "ready": "Verified draft ready. Publication requires the matching approved version tag.",
              "published": "Downloads verified against source CI artifacts and package checksums."}[metadata["phase"]]
    return (notes.rstrip() + "\n\n" + status + f"\n\nPrepared from `{metadata['sha']}` after "
            f"[master checks](https://github.com/{metadata['repository']}/actions/runs/{metadata['run_id']})."
            f"\n\n<!-- {MARKER} {json.dumps(metadata, sort_keys=True)} -->\n")


class Gh:
    def command(self, arguments, data=None):
        env = os.environ.copy()
        env.pop("GH_DEBUG", None)
        result = subprocess.run(["gh", *arguments], input=data, capture_output=True, env=env, check=False)
        require(result.returncode == 0, result.stderr.decode("utf-8", errors="replace").strip())
        return result.stdout

    def api(self, path, method="GET", payload=None):
        args = ["api", "--method", method, path]
        data = None
        if payload is not None:
            args += ["--input", "-"]
            data = json.dumps(payload).encode("utf-8")
        output = self.command(args, data)
        return json.loads(output) if output else None

    def download(self, path):
        return self.command(["api", "--method", "GET", path])

    def upload(self, repository, tag, path):
        self.command(["release", "upload", tag, str(path), "--repo", repository])


class Manager:
    def __init__(self, api, repository, version, head, notes):
        require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository), "Invalid repository")
        require(re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version), "Invalid release version")
        require(re.fullmatch(r"[0-9a-f]{40}", head), "Invalid source revision")
        require(notes.strip(), "Release notes are required")
        self.api, self.repository, self.version, self.head, self.notes = api, repository, version, head, notes
        self.root = f"repos/{repository}"
        self.tag = "v" + version

    def checked_run(self, run_id, packages=False):
        run = self.api.api(f"{self.root}/actions/runs/{int(run_id)}")
        expected_branch = self.tag if packages else "master"
        expected_path = ".github/workflows/release.yml" if packages else ".github/workflows/build.yml"
        require(run["event"] == "push" and run["head_branch"] == expected_branch
                and run["head_sha"] == self.head and run["path"] == expected_path
                and run["head_repository"]["full_name"] == self.repository,
                "Only this repository's matching master/tag push run is trusted")
        if not packages:
            require(run["status"] == "completed" and run["conclusion"] == "success", "Master checks not successful")
        jobs = self.api.api(f"{self.root}/actions/runs/{int(run_id)}/jobs?per_page=100")
        require(jobs["total_count"] <= 100, "Unexpected job count")
        required = PACKAGES if packages else CHECKS
        matches = [job for job in jobs["jobs"] if job["name"] in required]
        require(len(matches) == 2 and {job["name"] for job in matches} == required
                and all(job["status"] == "completed" and job["conclusion"] == "success" for job in matches),
                "Both platform jobs must pass")

    def current_master(self):
        require(self.api.api(f"{self.root}/branches/master")["commit"]["sha"] == self.head,
                "Master advanced; a newer successful run must prepare the draft")

    def find_release(self):
        # Include drafts with the normal authorized workflow token, without masking API errors as 404.
        results = self.api.api(f"{self.root}/releases?per_page=100")
        require(len(results) < 100, "Release listing requires pagination; refuse ambiguous lookup")
        matches = [release for release in results if release["tag_name"] == self.tag]
        # GitHub can return an untagged draft after a body-only readiness update.
        # Recover only our exact version's receipt, never an arbitrary user draft.
        for release in results:
            if not release.get("draft") or not re.fullmatch(r"untagged-[0-9a-f]+", release.get("tag_name", "")):
                continue
            body = release.get("body") or ""
            if MARKER not in body and release.get("name") != "Wyrmwatch " + self.tag:
                continue
            markers = re.findall(r"<!-- " + MARKER + r" (.*?) -->", body)
            if release.get("name") != "Wyrmwatch " + self.tag and len(markers) == 1:
                candidate = json.loads(markers[0])
                if candidate.get("repository") != self.repository or candidate.get("version") != self.version:
                    continue
            metadata = receipt(body, self.repository, self.version)
            require(release.get("target_commitish") == metadata["sha"], "Untagged draft target differs from receipt")
            matches.append(release)
        require(len(matches) <= 1, "Duplicate release tag")
        return matches[0] if matches else None

    def owned_draft(self, release_id, metadata):
        release = self.api.api(f"{self.root}/releases/{release_id}")
        actual = receipt(release.get("body"), self.repository, self.version)
        tag_matches = release["tag_name"] == self.tag or (
            re.fullmatch(r"untagged-[0-9a-f]+", release["tag_name"])
            and release.get("target_commitish") == metadata["sha"])
        require(release["draft"] and tag_matches and actual == metadata,
                "Draft state changed; refuse to mutate or publish")
        return release

    def tag_commit(self):
        ref = self.api.api(f"{self.root}/git/ref/tags/{self.tag}")["object"]
        for _ in range(4):
            if ref["type"] != "tag":
                break
            ref = self.api.api(f"{self.root}/git/tags/{ref['sha']}")["object"]
        require(ref["type"] == "commit", "Approved tag does not resolve to a commit")
        return ref["sha"]

    def approved_commit(self):
        require(self.tag_commit() == self.head, "Approved tag does not point at the tested commit")

    def prepare(self, run_id, _source_guard=None):
        def guard():
            if _source_guard is None:
                self.current_master()
            else:
                _source_guard()
                self.approved_commit()
        self.checked_run(run_id)
        guard()
        existing = self.find_release()
        if existing and not existing["draft"]:
            return "Version already published; bump the version for a new candidate"
        old = receipt(existing.get("body"), self.repository, self.version) if existing else None
        if old:
            validate_assets(existing, old, complete=old["phase"] != "preparing")
        refs = self.api.api(f"{self.root}/git/matching-refs/tags/{self.tag}")
        if any(ref["ref"] == "refs/tags/" + self.tag for ref in refs):
            approved = self.tag_commit()
            if approved != self.head:
                return f"{self.tag} already approved at {approved}; preserve its candidate and bump the version"
        artifacts = self.api.api(f"{self.root}/actions/runs/{int(run_id)}/artifacts?per_page=100")
        require(artifacts["total_count"] <= 100, "Unexpected artifact count")
        files = {}
        for runtime in ("win-x64", "linux-x64"):
            matches = [item for item in artifacts["artifacts"] if item["name"] == "downloads-" + runtime]
            require(len(matches) == 1 and not matches[0]["expired"], "Missing/duplicate/expired platform artifact")
            artifact = matches[0]
            require(artifact["size_in_bytes"] <= MAX_ARTIFACT, "Artifact exceeds size bound")
            blob = self.api.download(f"{self.root}/actions/artifacts/{artifact['id']}/zip")
            files.update(unpack_artifact(blob, artifact.get("digest"), expected_files(self.version, runtime)))
        hashes = {name: sha(data) for name, data in files.items()}
        if old and old["phase"] == "ready" and old["sha"] == self.head and old["assets"] == hashes \
                and existing["tag_name"] == self.tag:
            return "Matching draft already ready; no changes"
        previous = {asset["name"]: asset["digest"].removeprefix("sha256:")
                    for asset in existing.get("assets", [])} if existing else {}
        metadata = dict(repository=self.repository, version=self.version, sha=self.head, run_id=int(run_id),
                        phase="preparing", assets=hashes, previous_assets=previous)
        guard()
        values = dict(tag_name=self.tag, target_commitish=self.head, name="Wyrmwatch " + self.tag,
                      body=release_body(self.notes, metadata), draft=True, prerelease=False)
        if existing:
            self.owned_draft(existing["id"], old)
            release = self.api.api(f"{self.root}/releases/{existing['id']}", "PATCH", values)
        else:
            release = self.api.api(f"{self.root}/releases", "POST", values)
        with tempfile.TemporaryDirectory(prefix="wyrmwatch-release-") as directory:
            for name, data in sorted(files.items()):
                current = self.owned_draft(release["id"], metadata)
                validate_assets(current, metadata, complete=False)
                asset = next((asset for asset in current["assets"] if asset["name"] == name), None)
                if asset and asset["digest"] == "sha256:" + hashes[name]:
                    continue
                if asset:
                    self.api.api(f"{self.root}/releases/assets/{asset['id']}", "DELETE")
                path = Path(directory) / name
                path.write_bytes(data)
                self.owned_draft(release["id"], metadata)
                self.api.upload(self.repository, self.tag, path)
        current = self.owned_draft(release["id"], metadata)
        validate_assets(current, metadata)
        guard()
        metadata = dict(metadata, phase="ready", previous_assets={})
        self.api.api(f"{self.root}/releases/{release['id']}", "PATCH",
                     {"tag_name": self.tag, "target_commitish": self.head,
                      "draft": True, "body": release_body(self.notes, metadata)})
        confirmed = self.owned_draft(release["id"], metadata)
        require(confirmed["tag_name"] == self.tag, "GitHub did not preserve the ready draft's version tag")
        validate_assets(confirmed, metadata)
        guard()
        return f"Verified draft {release['id']} ready at {self.head}; approve its exact commit with {self.tag}"

    def publish(self, run_id, approved_tag, _before_publish=None):
        require(approved_tag == self.tag, "Approved tag must match the project version")
        self.checked_run(run_id, packages=True)
        self.approved_commit()
        release = self.find_release()
        require(release is not None, "Wait for the verified master draft before publishing this tag")
        metadata = receipt(release.get("body"), self.repository, self.version)
        require(metadata["sha"] == self.head, "Draft and approved tag have different source revisions")
        validate_assets(release, metadata)
        self.checked_run(metadata["run_id"])
        if not release["draft"]:
            require(metadata["phase"] == "published", "Unknown published release; refuse to alter it")
            return "Matching release already published; no changes"
        require(metadata["phase"] == "ready", "Draft is incomplete; refuse to publish")
        self.owned_draft(release["id"], metadata)
        if _before_publish is not None:
            _before_publish()
        metadata = dict(metadata, phase="published")
        published = self.api.api(f"{self.root}/releases/{release['id']}", "PATCH",
                                {"tag_name": self.tag, "target_commitish": self.head,
                                 "body": release_body(self.notes, metadata), "draft": False, "make_latest": "true"})
        require(not published["draft"] and published["tag_name"] == self.tag
                and receipt(published.get("body"), self.repository, self.version) == metadata,
                "GitHub did not confirm publication")
        validate_assets(published, metadata)
        return "Published verified " + self.tag + " from " + self.head

    def recover(self, run_id, approved_tag, verification_run_id, verified_head):
        # Recovery runs reviewed orchestration from current green master, but the
        # release source/assets remain pinned to the original approved tag.
        verifier = Manager(self.api, self.repository, self.version, verified_head, self.notes)
        verifier.checked_run(verification_run_id)
        verifier.current_master()
        return self.publish(run_id, approved_tag)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("prepare", "publish", "recover"))
    parser.add_argument("--run-id", type=int, required=True)
    parser.add_argument("--verification-run-id", type=int)
    parser.add_argument("--source-directory", choices=("release-source",))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    source = root / "release-source" if args.mode == "recover" else root
    if args.mode == "recover":
        require(args.source_directory == "release-source" and args.verification_run_id is not None,
                "Recovery requires its pinned source checkout and successful current master run")
        require(os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch"
                and os.environ.get("GITHUB_REF") == "refs/heads/master",
                "Recovery requires explicit dispatch from trusted master")
    else:
        require(args.source_directory is None and args.verification_run_id is None,
                "Source overrides are restricted to explicit approved-tag recovery")
    version = ET.parse(source / "Directory.Build.props").findtext("PropertyGroup/Version")
    require(version is not None and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version), "Invalid project version")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    manager = Manager(Gh(), os.environ["GITHUB_REPOSITORY"], version, head,
                      (source / "docs/releases" / ("v" + version + ".md")).read_text(encoding="utf-8"))
    if args.mode == "prepare":
        require(os.environ.get("GITHUB_EVENT_NAME") == "workflow_run", "Draft preparation requires workflow_run")
        result = manager.prepare(args.run_id)
    elif args.mode == "publish":
        require(os.environ.get("GITHUB_EVENT_NAME") == "push" and os.environ.get("GITHUB_REF_TYPE") == "tag",
                "Public publication requires an explicitly pushed version tag")
        result = manager.publish(args.run_id, os.environ["GITHUB_REF_NAME"])
    else:
        verified_head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        result = manager.recover(args.run_id, os.environ["SOURCE_TAG"], args.verification_run_id, verified_head)
    print(result)


if __name__ == "__main__":
    main()
