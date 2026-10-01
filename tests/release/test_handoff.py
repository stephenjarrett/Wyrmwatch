"""Automatic release event/provenance/race fixtures: never contact GitHub."""
import base64
import copy
import importlib.util
import os
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.parse import parse_qs

from test_release import FakeGh, HEAD, REPOSITORY, VERSION
import test_release

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("release_handoff", ROOT / "scripts/release-handoff.py")
handoff = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(handoff)
policy = handoff.policy


class HandoffGh(FakeGh):
    def __init__(self):
        super().__init__()
        self.runs = {}
        for run_id in (1, 2, 3):
            run = super().api(f"repos/{REPOSITORY}/actions/runs/{run_id}")
            run.update(id=run_id, status="completed", conclusion="success")
            self.runs[run_id] = run
        self.visible_runs = [1, 2]
        self.legacy_publish_failure = False
        self.extra_failed_job = False
        self.advance_after_packages = False
        self.move_tag_after_packages = False
        self.source_version = VERSION
        self.source_notes = "# Original pinned release notes"
        self.deny_api = False

    def api(self, path, method="GET", payload=None):
        if self.deny_api:
            raise policy.ReleaseError("Resource not accessible by integration")
        suffix = path.removeprefix("repos/" + REPOSITORY)
        if suffix.startswith("/actions/runs?"):
            query = parse_qs(suffix.split("?", 1)[1])
            matches = [copy.deepcopy(self.runs[run_id]) for run_id in self.visible_runs
                       if self.runs[run_id]["head_sha"] == query["head_sha"][0]
                       and self.runs[run_id]["head_branch"] == query["branch"][0]]
            return {"total_count": len(matches), "workflow_runs": matches}
        if suffix.startswith("/actions/runs/") and suffix.rsplit("/", 1)[1].isdigit():
            return copy.deepcopy(self.runs[int(suffix.rsplit("/", 1)[1])])
        if suffix.startswith("/contents/"):
            if suffix.startswith("/contents/Directory.Build.props?"):
                text = f"<Project><PropertyGroup><Version>{self.source_version}</Version></PropertyGroup></Project>"
            else:
                text = self.source_notes
            content = text.encode()
            return {"type": "file", "encoding": "base64", "size": len(content),
                    "content": base64.b64encode(content).decode()}
        result = super().api(path, method, payload)
        if suffix == "/actions/runs/2/jobs?per_page=100":
            if self.legacy_publish_failure:
                result["jobs"].append({"name": "publish", "status": "completed", "conclusion": "failure"})
                result["total_count"] += 1
            if self.extra_failed_job:
                result["jobs"].append({"name": "other safety check", "status": "completed", "conclusion": "failure"})
                result["total_count"] += 1
            if self.advance_after_packages:
                self.master = "c" * 40
            if self.move_tag_after_packages:
                self.tag_sha = "c" * 40
        return result


class HandoffTests(unittest.TestCase):
    def setUp(self):
        self.api = HandoffGh()
        self.manager = policy.Manager(self.api, REPOSITORY, VERSION, HEAD, "# Current master notes")
        self.handoff = handoff.Handoff(self.manager)

    def ready(self):
        self.manager.prepare(1)

    def approved(self):
        self.api.tag_exists = True

    def test_draft_before_tag_waits_then_publishes_on_tag_completion(self):
        result = self.handoff.run(1)
        self.assertIn("waiting for explicit", result)
        self.assertTrue(self.api.release["draft"])
        self.approved()
        result = self.handoff.run(2)
        self.assertIn("Published verified", result)
        self.assertFalse(self.api.release["draft"])
        self.assertIn(self.api.source_notes, self.api.release["body"])

    def test_tag_before_draft_waits_for_master_then_prepares_and_publishes(self):
        self.approved()
        self.api.runs[1]["status"] = "in_progress"
        result = self.handoff.run(2)
        self.assertIn("Waiting for successful checks", result)
        self.assertEqual([], self.api.mutations)
        self.api.runs[1]["status"] = "completed"
        self.handoff.run(1)
        self.assertFalse(self.api.release["draft"])

    def test_tag_event_can_prepare_when_master_completion_event_was_coalesced(self):
        self.approved()
        self.handoff.run(2)
        self.assertFalse(self.api.release["draft"])
        self.assertEqual(8, self.api.uploads)

    def test_ready_draft_waits_for_tag_packages_and_repeated_events_are_noops(self):
        self.ready(); self.approved()
        self.api.runs[2]["status"] = "in_progress"
        writes = len(self.api.mutations)
        self.assertIn("waiting for both", self.handoff.run(1))
        self.assertEqual(writes, len(self.api.mutations))
        self.api.runs[2]["status"] = "completed"
        self.handoff.run(2)
        writes = len(self.api.mutations)
        self.handoff.run(1); self.handoff.run(2); self.handoff.run()
        self.assertEqual(writes, len(self.api.mutations))
        self.assertEqual(8, self.api.uploads)

    def test_pending_approved_legacy_untagged_draft_recovers_after_new_master_checks(self):
        self.ready(); self.approved()
        self.api.release["tag_name"] = "untagged-e544df8a98720da5ed9b"
        self.api.master = self.api.verification_head = "b" * 40
        self.api.runs[3]["head_sha"] = "b" * 40
        self.api.visible_runs.append(3)
        self.api.runs[2]["conclusion"] = "failure"
        self.api.legacy_publish_failure = True
        assets = copy.deepcopy(self.api.release["assets"])
        verifier = policy.Manager(self.api, REPOSITORY, VERSION, "b" * 40, "# New orchestration notes")
        handoff.Handoff(verifier).run(3)
        self.assertFalse(self.api.release["draft"])
        self.assertEqual(assets, self.api.release["assets"])
        metadata = policy.receipt(self.api.release["body"], REPOSITORY, VERSION)
        self.assertEqual(HEAD, metadata["sha"])
        self.assertEqual(1, metadata["run_id"])

    def test_tag_approved_old_source_without_draft_can_prepare_from_pinned_artifacts(self):
        self.approved()
        self.api.master = self.api.verification_head = "b" * 40
        self.api.runs[3]["head_sha"] = "b" * 40
        self.api.visible_runs.append(3)
        verifier = policy.Manager(self.api, REPOSITORY, VERSION, "b" * 40, "# New orchestration notes")
        handoff.Handoff(verifier).run(3)
        metadata = policy.receipt(self.api.release["body"], REPOSITORY, VERSION)
        self.assertEqual(HEAD, metadata["sha"])
        self.assertEqual(1, metadata["run_id"])
        self.assertFalse(self.api.release["draft"])

    def test_interrupted_preparation_is_unpublished_and_resumes_without_overwrite(self):
        self.approved(); self.api.fail_upload = 3
        with self.assertRaises(test_release.release.ReleaseError): self.handoff.run(1)
        self.assertTrue(self.api.release["draft"])
        self.api.fail_upload = None
        self.handoff.run(1)
        self.assertFalse(self.api.release["draft"])
        self.assertEqual(8, len(self.api.release["assets"]))

    def test_stale_and_superseded_events_make_no_changes(self):
        for alteration in ("master", "version"):
            with self.subTest(alteration=alteration):
                self.setUp()
                run_id = 1 if alteration == "master" else 2
                if alteration == "master": self.api.runs[1]["head_sha"] = "b" * 40
                else: self.api.runs[2]["head_branch"] = "v0.2.2"
                self.assertIn("Stale or superseded", self.handoff.run(run_id))
                self.assertEqual([], self.api.mutations)

    def test_tag_event_for_a_different_revision_is_not_used(self):
        self.approved()
        self.api.runs[2]["head_sha"] = "b" * 40
        self.assertIn("Stale approved-tag event", self.handoff.run(2))
        self.assertEqual([], self.api.mutations)

    def test_pr_foreign_and_wrong_workflow_triggers_fail_closed(self):
        for alteration in ("event", "repo", "path", "branch"):
            with self.subTest(alteration=alteration):
                self.setUp()
                run = self.api.runs[1]
                if alteration == "event": run["event"] = "pull_request"
                elif alteration == "repo": run["head_repository"]["full_name"] = "foreign/repo"
                elif alteration == "path": run["path"] = ".github/workflows/untrusted.yml"
                else: run["head_branch"] = "sjarrett/fixture"
                with self.assertRaises(policy.ReleaseError): self.handoff.run(1)
                self.assertEqual([], self.api.mutations)

    def test_failed_master_and_nonpush_discovery_do_not_prepare(self):
        for alteration in ("failed", "pr", "foreign"):
            with self.subTest(alteration=alteration):
                self.setUp()
                if alteration == "failed": self.api.runs[1]["conclusion"] = "failure"
                elif alteration == "pr": self.api.runs[1]["event"] = "pull_request"
                else: self.api.runs[1]["head_repository"]["full_name"] = "foreign/repo"
                self.assertIn("Waiting for successful", self.handoff.run())
                self.assertEqual([], self.api.mutations)

    def test_multiple_matching_master_push_runs_are_ambiguous(self):
        self.api.visible_runs.append(3)
        with self.assertRaises(policy.ReleaseError): self.handoff.run()
        self.assertEqual([], self.api.mutations)

    def test_multiple_matching_tag_push_runs_are_ambiguous(self):
        self.ready(); self.approved()
        self.api.runs[4] = dict(copy.deepcopy(self.api.runs[2]), id=4)
        self.api.visible_runs.append(4)
        writes = len(self.api.mutations)
        with self.assertRaises(policy.ReleaseError): self.handoff.run()
        self.assertEqual(writes, len(self.api.mutations))

    def test_untrusted_package_discovery_never_publishes(self):
        for alteration in ("event", "repo", "branch", "head"):
            with self.subTest(alteration=alteration):
                self.setUp(); self.ready(); self.approved()
                run = self.api.runs[2]
                if alteration == "event": run["event"] = "workflow_dispatch"
                elif alteration == "repo": run["head_repository"]["full_name"] = "foreign/repo"
                elif alteration == "branch": run["head_branch"] = "v0.2.2"
                else: run["head_sha"] = "b" * 40
                writes = len(self.api.mutations)
                self.assertIn("waiting for both", self.handoff.run())
                self.assertEqual(writes, len(self.api.mutations))

    def test_pinned_source_metadata_is_bounded_and_validated(self):
        for alteration in ("size", "encoding", "type", "base64", "length"):
            with self.subTest(alteration=alteration):
                value = {"type": "file", "encoding": "base64", "size": 1, "content": "eA=="}
                if alteration == "size": value["size"] = 1024 * 1024 + 1
                elif alteration == "encoding": value["encoding"] = "none"
                elif alteration == "type": value["type"] = "symlink"
                elif alteration == "base64": value["content"] = "!invalid!"
                else: value["size"] = 2
                with patch.object(self.api, "api", return_value=value):
                    with self.assertRaises((policy.ReleaseError, ValueError)):
                        self.handoff.source_text("Directory.Build.props", HEAD)

    def test_failed_package_or_other_guard_never_publishes(self):
        for alteration in ("package", "other", "cancelled"):
            with self.subTest(alteration=alteration):
                self.setUp(); self.ready(); self.approved()
                self.api.runs[2]["conclusion"] = "failure"
                if alteration == "package": self.api.failed_packages = True
                elif alteration == "other": self.api.extra_failed_job = True
                else: self.api.runs[2]["conclusion"] = "cancelled"
                writes = len(self.api.mutations)
                with self.assertRaises(policy.ReleaseError): self.handoff.run()
                self.assertEqual(writes, len(self.api.mutations))

    def test_tag_or_master_advance_before_publication_blocks_all_writes(self):
        for attribute in ("advance_after_packages", "move_tag_after_packages"):
            with self.subTest(attribute=attribute):
                self.setUp(); self.ready(); self.approved()
                setattr(self.api, attribute, True)
                writes = len(self.api.mutations)
                with self.assertRaises(policy.ReleaseError): self.handoff.run()
                self.assertEqual(writes, len(self.api.mutations))

    def test_source_version_mismatch_unknown_draft_and_changed_assets_are_refused(self):
        for alteration in ("version", "body", "sha", "assets"):
            with self.subTest(alteration=alteration):
                self.setUp(); self.ready(); self.approved()
                if alteration == "version": self.api.source_version = "0.2.2"
                elif alteration == "body": self.api.release["body"] = "User draft"
                elif alteration == "sha": self.api.release["body"] = self.api.release["body"].replace(HEAD, "b" * 40)
                else: self.api.release["assets"].pop()
                writes = len(self.api.mutations)
                with self.assertRaises(policy.ReleaseError): self.handoff.run()
                self.assertEqual(writes, len(self.api.mutations))

    def test_newer_public_release_is_not_superseded(self):
        self.api.extra_releases.append({"tag_name": "v0.2.5", "draft": False, "prerelease": False})
        with self.assertRaises(policy.ReleaseError): self.handoff.run()
        self.assertEqual([], self.api.mutations)

    def test_permission_denial_is_not_masked_or_retried(self):
        self.api.deny_api = True
        with self.assertRaisesRegex(policy.ReleaseError, "Resource not accessible"): self.handoff.run()
        self.assertEqual([], self.api.mutations)

    def test_cli_refuses_untrusted_branch_and_event_before_checkout_reads(self):
        for event, ref in (("push", "refs/heads/master"), ("workflow_run", "refs/heads/sjarrett/fixture")):
            with self.subTest(event=event), patch.dict(os.environ, GITHUB_EVENT_NAME=event, GITHUB_REF=ref), \
                    patch.object(handoff.subprocess, "check_output") as git:
                with self.assertRaises(policy.ReleaseError): handoff.main()
                git.assert_not_called()


if __name__ == "__main__":
    unittest.main()
