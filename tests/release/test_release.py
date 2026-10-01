"""Offline fault/ownership tests: never call GitHub or publish anything."""
import copy
import importlib.util
import io
import json
import stat
import unittest
import zipfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("manage_release", ROOT / "scripts/manage-release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)
REPOSITORY, HEAD, VERSION = "fixture/Wyrmwatch", "a" * 40, "0.2.3"


def archive(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as zipped:
        for name, data in files.items():
            zipped.writestr(name, data)
    return stream.getvalue()


class FakeGh:
    def __init__(self):
        self.head = self.master = self.tag_sha = HEAD
        self.release = None
        self.mutations = []
        self.uploads = 0
        self.fail_upload = None
        self.advance_during_upload = False
        self.publish_on_read = False
        self.artifacts, self.blobs = [], {}
        for artifact_id, runtime in enumerate(("win-x64", "linux-x64"), 1):
            files = {}
            for name in release.expected_files(VERSION, runtime):
                if not name.endswith(".sha256"):
                    data = ("disposable " + name).encode()
                    files[name] = data
                    files[name + ".sha256"] = f"{release.sha(data)}  {name}\n".encode()
            blob = archive(files)
            self.blobs[artifact_id] = blob
            self.artifacts.append(dict(id=artifact_id, name="downloads-" + runtime, expired=False,
                                       size_in_bytes=len(blob), digest="sha256:" + release.sha(blob)))
        self.failed_master = self.failed_packages = False
        self.master_event = "push"
        self.foreign_repository = False

    def api(self, path, method="GET", payload=None):
        prefix = "repos/" + REPOSITORY
        path = path.removeprefix(prefix)
        if method != "GET":
            self.mutations.append((path, method, copy.deepcopy(payload)))
        if path == "/branches/master":
            return {"commit": {"sha": self.master}}
        if path == "/releases?per_page=100":
            return [copy.deepcopy(self.release)] if self.release else []
        if path == "/releases" and method == "POST":
            self.release = dict(payload, id=10, assets=[])
            return copy.deepcopy(self.release)
        if path == "/releases/10":
            if method == "PATCH":
                self.release.update(payload)
            elif self.publish_on_read:
                self.release["draft"] = False
            return copy.deepcopy(self.release)
        if path.startswith("/releases/assets/") and method == "DELETE":
            asset_id = int(path.rsplit("/", 1)[1])
            self.release["assets"] = [asset for asset in self.release["assets"] if asset["id"] != asset_id]
            return None
        if path == "/git/ref/tags/v" + VERSION:
            return {"object": {"type": "commit", "sha": self.tag_sha}}
        if path in ("/actions/runs/1", "/actions/runs/2"):
            package = path.endswith("2")
            return dict(event="push" if package else self.master_event, head_sha=self.head,
                        head_branch="v" + VERSION if package else "master",
                        head_repository={"full_name": "foreign/repo" if self.foreign_repository else REPOSITORY},
                        path=".github/workflows/release.yml" if package else ".github/workflows/build.yml",
                        status="in_progress" if package else "completed",
                        conclusion=None if package else ("failure" if self.failed_master else "success"))
        if path in ("/actions/runs/1/jobs?per_page=100", "/actions/runs/2/jobs?per_page=100"):
            package = "/2/" in path
            return {"total_count": 2, "jobs": [dict(name=name, status="completed",
                    conclusion="failure" if (self.failed_packages if package else self.failed_master) else "success")
                    for name in (release.PACKAGES if package else release.CHECKS)]}
        if path == "/actions/runs/1/artifacts?per_page=100":
            return {"total_count": len(self.artifacts), "artifacts": self.artifacts}
        raise AssertionError("Unexpected API call: " + path + " " + method)

    def download(self, path):
        return self.blobs[int(path.split("/")[-2])]

    def upload(self, repository, tag, path):
        assert repository == REPOSITORY and tag == "v" + VERSION and self.release["draft"]
        self.uploads += 1
        if self.uploads == self.fail_upload:
            raise release.ReleaseError("Simulated interrupted upload")
        self.release["assets"].append(dict(id=100 + self.uploads, name=path.name,
                                           digest="sha256:" + release.sha(path.read_bytes())))
        if self.advance_during_upload:
            self.master = "b" * 40


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.api = FakeGh()
        self.manager = release.Manager(self.api, REPOSITORY, VERSION, HEAD, "# Fixture release notes")

    def ready(self):
        self.manager.prepare(1)
        return release.receipt(self.api.release["body"], REPOSITORY, VERSION)

    def test_prepare_is_draft_only_and_idempotent(self):
        metadata = self.ready()
        self.assertTrue(self.api.release["draft"])
        self.assertEqual("ready", metadata["phase"])
        self.assertEqual(HEAD, metadata["sha"])
        self.assertEqual(8, len(self.api.release["assets"]))
        writes = len(self.api.mutations)
        self.manager.prepare(1)
        self.assertEqual(writes, len(self.api.mutations))
        self.assertEqual(8, self.api.uploads)

    def test_approved_tag_publishes_prepared_assets_once(self):
        self.ready()
        original_assets = copy.deepcopy(self.api.release["assets"])
        self.manager.publish(2, "v" + VERSION)
        self.assertFalse(self.api.release["draft"])
        self.assertEqual(original_assets, self.api.release["assets"])
        writes = len(self.api.mutations)
        self.manager.publish(2, "v" + VERSION)
        self.assertEqual(writes, len(self.api.mutations))

    def test_interrupted_upload_stays_unpublishable_and_resumes(self):
        self.api.fail_upload = 3
        with self.assertRaises(release.ReleaseError):
            self.manager.prepare(1)
        self.assertEqual("preparing", release.receipt(self.api.release["body"], REPOSITORY, VERSION)["phase"])
        with self.assertRaises(release.ReleaseError):
            self.manager.publish(2, "v" + VERSION)
        self.assertTrue(self.api.release["draft"])
        self.api.fail_upload = None
        self.ready()
        self.assertEqual(8, len(self.api.release["assets"]))
        self.manager.publish(2, "v" + VERSION)
        self.assertFalse(self.api.release["draft"])

    def test_stale_master_refuses_all_mutations(self):
        self.api.master = "b" * 40
        with self.assertRaises(release.ReleaseError):
            self.manager.prepare(1)
        self.assertEqual([], self.api.mutations)

    def test_master_advance_during_upload_leaves_incomplete_draft(self):
        self.api.advance_during_upload = True
        with self.assertRaises(release.ReleaseError):
            self.manager.prepare(1)
        self.assertTrue(self.api.release["draft"])
        self.assertEqual("preparing", release.receipt(self.api.release["body"], REPOSITORY, VERSION)["phase"])

    def test_failure_pr_or_foreign_run_cannot_prepare(self):
        for attribute, value in (("failed_master", True), ("master_event", "pull_request"), ("foreign_repository", True)):
            with self.subTest(attribute=attribute):
                api = FakeGh(); setattr(api, attribute, value)
                with self.assertRaises(release.ReleaseError):
                    release.Manager(api, REPOSITORY, VERSION, HEAD, "Notes").prepare(1)
                self.assertEqual([], api.mutations)

    def test_archive_digest_or_expiry_blocks_draft_creation(self):
        for alteration in ("digest", "expiry", "duplicate"):
            with self.subTest(alteration=alteration):
                api = FakeGh()
                if alteration == "digest": api.artifacts[0]["digest"] = "sha256:" + "0" * 64
                elif alteration == "expiry": api.artifacts[0]["expired"] = True
                else: api.artifacts.append(copy.deepcopy(api.artifacts[0]))
                with self.assertRaises(release.ReleaseError):
                    release.Manager(api, REPOSITORY, VERSION, HEAD, "Notes").prepare(1)
                self.assertEqual([], api.mutations)

    def test_package_checksum_mismatch_blocks_mutation(self):
        blob = self.api.blobs[1]
        with zipfile.ZipFile(io.BytesIO(blob)) as zipped:
            files = {name: zipped.read(name) for name in zipped.namelist()}
        files["Wyrmwatch-win-x64.zip"] += b"damaged"
        blob = archive(files); self.api.blobs[1] = blob
        self.api.artifacts[0]["digest"] = "sha256:" + release.sha(blob)
        with self.assertRaises(release.ReleaseError): self.manager.prepare(1)
        self.assertEqual([], self.api.mutations)

    def test_unknown_draft_is_never_adopted(self):
        self.api.release = dict(id=10, tag_name="v" + VERSION, draft=True, body="User draft", assets=[])
        with self.assertRaises(release.ReleaseError): self.manager.prepare(1)
        self.assertEqual([], self.api.mutations)

    def test_unknown_or_modified_assets_block_prepare_and_publish(self):
        for alteration in ("unknown", "digest", "missing"):
            with self.subTest(alteration=alteration):
                self.setUp(); self.ready()
                if alteration == "unknown": self.api.release["assets"][0]["name"] = "user-file.txt"
                elif alteration == "digest": self.api.release["assets"][0]["digest"] = "sha256:" + "0" * 64
                else: self.api.release["assets"].pop()
                writes = len(self.api.mutations)
                with self.assertRaises(release.ReleaseError): self.manager.publish(2, "v" + VERSION)
                with self.assertRaises(release.ReleaseError): self.manager.prepare(1)
                self.assertEqual(writes, len(self.api.mutations))

    def test_failed_release_checks_or_wrong_tag_cannot_publish(self):
        self.ready()
        for alteration in ("packages", "tag", "revision"):
            with self.subTest(alteration=alteration):
                self.api.failed_packages = alteration == "packages"
                self.api.tag_sha = "b" * 40 if alteration == "revision" else HEAD
                with self.assertRaises(release.ReleaseError):
                    self.manager.publish(2, "v9.9.9" if alteration == "tag" else "v" + VERSION)
                self.assertTrue(self.api.release["draft"])

    def test_published_version_is_not_overwritten_by_new_master(self):
        self.ready(); self.manager.publish(2, "v" + VERSION)
        previous = copy.deepcopy(self.api.release)
        self.api.head = self.api.master = "b" * 40
        release.Manager(self.api, REPOSITORY, VERSION, "b" * 40, "New notes").prepare(1)
        self.assertEqual(previous, self.api.release)

    def test_new_verified_master_updates_only_owned_draft(self):
        self.ready()
        old_digest = next(asset["digest"] for asset in self.api.release["assets"]
                          if asset["name"] == "Wyrmwatch-win-x64.zip")
        with zipfile.ZipFile(io.BytesIO(self.api.blobs[1])) as zipped:
            files = {name: zipped.read(name) for name in zipped.namelist()}
        name = "Wyrmwatch-win-x64.zip"
        files[name] = b"new verified disposable build"
        files[name + ".sha256"] = f"{release.sha(files[name])}  {name}\n".encode()
        self.api.blobs[1] = archive(files)
        self.api.artifacts[0]["digest"] = "sha256:" + release.sha(self.api.blobs[1])
        self.api.head = self.api.master = "b" * 40
        release.Manager(self.api, REPOSITORY, VERSION, "b" * 40, "New approved notes").prepare(1)
        metadata = release.receipt(self.api.release["body"], REPOSITORY, VERSION)
        self.assertEqual("b" * 40, metadata["sha"])
        self.assertEqual("ready", metadata["phase"])
        self.assertTrue(self.api.release["draft"])
        self.assertEqual(8, len(self.api.release["assets"]))
        self.assertNotEqual(old_digest, next(asset["digest"] for asset in self.api.release["assets"] if asset["name"] == name))

    def test_remote_draft_state_change_blocks_publication_mutation(self):
        self.ready()
        writes = len(self.api.mutations)
        self.api.publish_on_read = True
        with self.assertRaises(release.ReleaseError): self.manager.publish(2, "v" + VERSION)
        self.assertEqual(writes, len(self.api.mutations))

    def test_unsafe_zip_paths_duplicates_and_links_are_refused(self):
        for alteration in ("path", "duplicate", "link"):
            with self.subTest(alteration=alteration):
                stream = io.BytesIO()
                with zipfile.ZipFile(stream, "w") as zipped:
                    if alteration == "path": zipped.writestr("../fixture.txt", b"fixture")
                    elif alteration == "duplicate":
                        zipped.writestr("fixture.txt", b"one"); zipped.writestr("fixture.txt", b"two")
                    else:
                        info = zipfile.ZipInfo("fixture.txt"); info.external_attr = (stat.S_IFLNK | 0o777) << 16
                        zipped.writestr(info, b"target")
                blob = stream.getvalue()
                with self.assertRaises(release.ReleaseError):
                    release.unpack_artifact(blob, "sha256:" + release.sha(blob), {"fixture.txt"})


class WorkflowPolicyTests(unittest.TestCase):
    def load(self, name):
        return yaml.load((ROOT / ".github/workflows" / name).read_text(), Loader=yaml.BaseLoader)

    def test_draft_scope_and_shared_non_cancelling_lock(self):
        draft = self.load("release-draft.yml"); public = self.load("release.yml")
        trigger = draft["on"]["workflow_run"]
        self.assertEqual(["master"], trigger["branches"])
        self.assertEqual(["completed"], trigger["types"])
        self.assertEqual(["Checks and portable builds"], trigger["workflows"])
        self.assertEqual(draft["concurrency"], public["concurrency"])
        self.assertEqual("false", draft["concurrency"]["cancel-in-progress"])
        job = draft["jobs"]["prepare"]
        for guard in ("conclusion == 'success'", "event == 'push'", "head_branch == 'master'",
                      "head_repository.full_name == github.repository"):
            self.assertIn(guard, job["if"])
        self.assertEqual({"contents": "write", "actions": "read"}, job["permissions"])
        self.assertEqual("${{ github.event.workflow_run.head_sha }}", job["steps"][0]["with"]["ref"])
        self.assertEqual("false", job["steps"][0]["with"]["persist-credentials"])

    def test_publication_requires_tag_and_both_package_jobs(self):
        workflow = self.load("release.yml")
        self.assertEqual({"push": {"tags": ["v*"]}}, workflow["on"])
        self.assertEqual({"contents": "read"}, workflow["permissions"])
        job = workflow["jobs"]["publish"]
        self.assertEqual("package", job["needs"])
        self.assertEqual({"contents": "write", "actions": "read"}, job["permissions"])
        self.assertIn("manage-release.py publish", job["steps"][-1]["run"])
        self.assertNotIn("gh release create", str(workflow))


if __name__ == "__main__":
    unittest.main()
