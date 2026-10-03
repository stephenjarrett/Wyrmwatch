"""Offline automatic version/tag/dispatch recovery and mutation-boundary fixtures."""
import base64
import copy
import hashlib
import importlib.util
import json
import unittest
from unittest.mock import patch

from test_handoff import HandoffGh, handoff, policy, ROOT
from test_release import HEAD, VERSION, REPOSITORY
import test_release

SPEC = importlib.util.spec_from_file_location("automatic_release", ROOT / "scripts/automatic_release.py")
automatic = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(automatic)


class AutoGh(HandoffGh):
    def __init__(self):
        super().__init__()
        self.refs = []
        self.files = {HEAD: {
            automatic.PROPS: f"<Project><PropertyGroup><Version>{VERSION}</Version></PropertyGroup></Project>",
            automatic.HOST: f'public const string Version = "{VERSION}";',
            automatic.LANGUAGE: f'{{"version":"v{VERSION} · Preview"}}',
            f"docs/releases/v{VERSION}.md": "# Original pinned release notes",
            "unchanged.cs": "protected application code",
        }}
        self.commits = {HEAD: {"message": "User merge", "parents": [], "tree": {"sha": HEAD}}}
        self.staged = None
        self.advance_before_ref = False
        self.fail_dispatch = False
        self.new_head = "d" * 40

    def api(self, path, method="GET", payload=None):
        suffix = path.removeprefix("repos/" + REPOSITORY)
        if suffix.startswith("/contents/"):
            name, head = suffix.removeprefix("/contents/").split("?ref=")
            content = self.files[head][name].encode()
            return {"type": "file", "encoding": "base64", "size": len(content), "content": base64.b64encode(content).decode()}
        if suffix == "/git/matching-refs/tags/v":
            return copy.deepcopy(self.refs)
        if suffix == "/git/matching-refs/tags/v" + VERSION:
            return [r for r in self.refs if r["ref"] == "refs/tags/v" + VERSION]
        if suffix.startswith("/git/commits/"):
            return copy.deepcopy(self.commits[suffix.rsplit("/", 1)[1]])
        if suffix.startswith("/git/trees/"):
            head = suffix.rsplit("/", 1)[1].split("?", 1)[0]
            if head == "e" * 40: head = self.new_head
            return {"truncated": False, "tree": [dict(path=p, mode="100644", type="blob", sha=hashlib.sha1(c.encode()).hexdigest()) for p, c in self.files[head].items()]}
        own_write = suffix in {"/git/trees", "/git/commits", "/git/refs", "/git/refs/heads/master"} or suffix.endswith("/dispatches")
        if own_write:
            self.mutations.append((suffix, method, copy.deepcopy(payload)))
        if suffix == "/git/trees" and method == "POST":
            self.staged = copy.deepcopy(self.files[payload["base_tree"]])
            for entry in payload["tree"]: self.staged[entry["path"]] = entry["content"]
            return {"sha": "e" * 40}
        if suffix == "/git/commits" and method == "POST":
            self.files[self.new_head] = self.staged
            self.commits[self.new_head] = dict(payload)
            self.commits[self.new_head]["tree"] = {"sha": payload["tree"]}
            self.commits[self.new_head]["parents"] = [{"sha": p} for p in payload["parents"]]
            if self.advance_before_ref: self.master = "f" * 40
            return {"sha": self.new_head}
        if suffix == "/git/refs/heads/master" and method == "PATCH":
            assert payload["force"] is False
            assert self.master == self.commits[payload["sha"]]["parents"][0]["sha"]
            self.master = payload["sha"]
            return {"object": {"sha": self.master}}
        if suffix == "/git/refs" and method == "POST":
            assert not any(r["ref"] == payload["ref"] for r in self.refs)
            self.refs.append({"ref": payload["ref"]})
            self.tag_exists = True; self.tag_sha = payload["sha"]
            return {"ref": payload["ref"], "object": {"sha": payload["sha"]}}
        if suffix.endswith("/dispatches"):
            if self.fail_dispatch: raise policy.ReleaseError("Simulated dispatch denial")
            return None
        return super().api(path, method, payload)


class AutomaticTests(unittest.TestCase):
    def setUp(self):
        self.api = AutoGh()
        self.manager = policy.Manager(self.api, REPOSITORY, VERSION, HEAD, "# Notes")
        self.handoff = handoff.Handoff(self.manager)
        self.auto = automatic.AutomaticRelease(self.handoff, policy)

    def reserve(self, head="b" * 40):
        self.api.refs = [{"ref": "refs/tags/v" + VERSION}]
        self.api.tag_exists = True; self.api.tag_sha = head

    def generated(self):
        self.reserve()
        self.auto.run(1)
        manager = policy.Manager(self.api, REPOSITORY, "0.2.5", self.api.new_head, "Notes")
        return automatic.AutomaticRelease(handoff.Handoff(manager), policy)

    def test_used_version_gets_one_metadata_commit_and_explicit_checks(self):
        self.reserve()
        result = self.auto.run(1)
        self.assertIn("Advanced master to v0.2.5", result)
        self.assertEqual("b" * 40, self.api.tag_sha)
        self.assertEqual("protected application code", self.api.files[self.api.new_head]["unchanged.cs"])
        changed = {p for p in self.api.files[self.api.new_head] if self.api.files[self.api.new_head][p] != self.api.files[HEAD].get(p)}
        self.assertEqual({automatic.PROPS, automatic.HOST, automatic.LANGUAGE, "docs/releases/v0.2.5.md"}, changed)
        dispatch = [w for w in self.api.mutations if w[0].endswith("/dispatches")]
        self.assertEqual(1, len(dispatch)); self.assertEqual({"ref": "master"}, dispatch[0][2])
        self.assertIsNone(self.api.release)

    def test_reserved_and_held_versions_are_never_reused(self):
        refs = [{"ref": "refs/tags/v0.2.5"}, {"ref": "refs/tags/v0.2.6"}]
        self.api.files[HEAD]["docs/releases/v0.2.8.md"] = "User's planned notes must be preserved"
        self.assertEqual("0.2.9", self.auto.next_version([{"tag_name": "v0.2.7", "draft": True}], refs))

    def test_concurrent_master_advance_never_force_updates_or_dispatches(self):
        self.reserve(); self.api.advance_before_ref = True
        with self.assertRaisesRegex(policy.ReleaseError, "Master advanced"): self.auto.run(1)
        self.assertFalse(any(w[0].endswith("/dispatches") or w[0] == "/git/refs/heads/master" for w in self.api.mutations))

    def test_existing_tag_and_hold_are_never_changed(self):
        self.reserve(); self.auto.run(1)
        self.assertFalse(any(w[0].startswith("/git/ref/tags") for w in self.api.mutations))
        held = policy.Manager(self.api, REPOSITORY, "0.2.3", HEAD, "Notes")
        writes = len(self.api.mutations)
        with self.assertRaises(policy.ReleaseError): automatic.AutomaticRelease(handoff.Handoff(held), policy).run()
        self.assertEqual(writes, len(self.api.mutations))

    def test_ready_draft_is_tagged_once_and_pending_package_checks_are_not_dispatched(self):
        self.api.visible_runs = [1]
        self.assertIn("Dispatched", self.auto.run(1))
        self.assertTrue(self.api.release["draft"])
        self.assertEqual(HEAD, self.api.tag_sha)
        dispatch = self.api.mutations[-1]
        self.assertEqual({"ref": "master", "inputs": {"approved_tag": "v" + VERSION}}, dispatch[2])
        self.api.visible_runs.append(2); self.api.runs[2]["status"] = "in_progress"
        writes = len(self.api.mutations)
        self.assertIn("waiting for both", self.auto.run(1))
        self.assertEqual(writes, len(self.api.mutations))

    def test_complete_package_dispatch_publishes_and_repeated_events_do_not_loop(self):
        self.api.visible_runs = [1]; self.auto.run(1)
        self.api.runs[2].update(event="workflow_dispatch", head_branch="master")
        self.api.visible_runs.append(2)
        self.assertIn("Published verified", self.auto.run(2))
        writes = len(self.api.mutations)
        self.auto.run(); self.auto.run(1); self.auto.run(2)
        self.assertEqual(writes, len(self.api.mutations))
        self.assertEqual(HEAD, self.api.master)

    def test_interrupted_version_dispatch_recovers_only_exact_own_metadata(self):
        self.api.fail_dispatch = True
        self.reserve()
        with self.assertRaisesRegex(policy.ReleaseError, "dispatch denial"): self.auto.run(1)
        manager = policy.Manager(self.api, REPOSITORY, "0.2.5", self.api.new_head, "Notes")
        auto = automatic.AutomaticRelease(handoff.Handoff(manager), policy)
        self.api.fail_dispatch = False
        self.assertTrue(auto.recover_version_commit())
        self.assertIn("Dispatched", auto.run())

    def test_recovery_rejects_unrelated_code_or_changed_version_metadata(self):
        for path in ("unchanged.cs", automatic.PROPS, automatic.HOST, automatic.LANGUAGE, "docs/releases/v0.2.5.md"):
            with self.subTest(path=path):
                self.setUp(); auto = self.generated()
                self.api.files[self.api.new_head][path] += " tampered"
                writes = len(self.api.mutations)
                with self.assertRaises(policy.ReleaseError): auto.run()
                self.assertEqual(writes, len(self.api.mutations))

    def test_failed_and_pending_generated_checks_never_rerun(self):
        for status, conclusion in (("queued", None), ("in_progress", None), ("completed", "failure"), ("completed", "cancelled")):
            with self.subTest(status=status, conclusion=conclusion):
                self.setUp(); auto = self.generated()
                self.api.runs[3].update(head_sha=self.api.new_head, event="workflow_dispatch", status=status, conclusion=conclusion)
                self.api.visible_runs.append(3)
                writes = len(self.api.mutations); auto.run()
                self.assertEqual(writes, len(self.api.mutations))

    def test_unknown_commit_without_checks_is_not_dispatched(self):
        self.api.visible_runs = []
        self.assertIn("Waiting", self.auto.run()); self.assertEqual([], self.api.mutations)

    def test_failed_upload_does_not_tag_and_interrupted_tag_dispatch_resumes(self):
        self.api.visible_runs = [1]; self.api.fail_upload = 2
        with self.assertRaises(test_release.release.ReleaseError): self.auto.run(1)
        self.assertEqual([], self.api.refs)
        self.api.fail_upload = None; self.api.fail_dispatch = True
        with self.assertRaisesRegex(policy.ReleaseError, "dispatch denial"): self.auto.run(1)
        self.assertEqual(HEAD, self.api.tag_sha); assets = copy.deepcopy(self.api.release["assets"])
        self.api.fail_dispatch = False; self.auto.run()
        self.assertEqual(assets, self.api.release["assets"])
        self.assertEqual(1, sum(w[0] == "/git/refs" for w in self.api.mutations))

    def test_unknown_draft_and_permission_denial_fail_closed(self):
        self.manager.prepare(1); self.api.release["body"] = "User draft"
        writes = len(self.api.mutations)
        with self.assertRaises(policy.ReleaseError): self.auto.run(1)
        self.assertEqual(writes, len(self.api.mutations))
        self.setUp(); self.api.deny_api = True
        with self.assertRaises(policy.ReleaseError): self.auto.run(1)
        self.assertEqual([], self.api.mutations)

    def test_branch_protection_denial_does_not_dispatch_or_retry(self):
        self.reserve(); original = self.api.api
        def guarded(path, method="GET", payload=None):
            if path.endswith("/git/refs/heads/master"):
                raise policy.ReleaseError("Protected branch requires a pull request")
            return original(path, method, payload)
        with patch.object(self.api, "api", side_effect=guarded):
            with self.assertRaisesRegex(policy.ReleaseError, "Protected branch"): self.auto.run(1)
        self.assertEqual(HEAD, self.api.master)
        self.assertFalse(any(w[0].endswith("/dispatches") for w in self.api.mutations))

    def test_recovery_rejects_unverified_parent_and_truncated_or_changed_modes(self):
        for fault in ("parent", "truncated", "mode"):
            with self.subTest(fault=fault):
                self.setUp(); auto = self.generated(); writes = len(self.api.mutations)
                original = self.api.api
                def damaged(path, method="GET", payload=None):
                    value = original(path, method, payload)
                    if fault == "parent" and path.endswith("/actions/runs/1"):
                        value["conclusion"] = "failure"
                    if "/git/trees/" in path:
                        if fault == "truncated": value["truncated"] = True
                        if fault == "mode":
                            next(e for e in value["tree"] if e["path"] == automatic.HOST)["mode"] = "100755"
                    return value
                with patch.object(self.api, "api", side_effect=damaged):
                    with self.assertRaises(policy.ReleaseError): auto.run()
                self.assertEqual(writes, len(self.api.mutations))

    def test_failed_package_dispatch_and_stale_events_never_publish_or_bump(self):
        self.api.visible_runs = [1]; self.auto.run(1)
        self.api.runs[2].update(event="workflow_dispatch", head_branch="master", conclusion="failure")
        self.api.visible_runs.append(2); self.api.failed_packages = True
        writes = len(self.api.mutations)
        with self.assertRaises(policy.ReleaseError): self.auto.run(2)
        self.assertEqual(writes, len(self.api.mutations)); self.assertTrue(self.api.release["draft"])
        self.api.runs[1]["head_sha"] = "b" * 40
        self.assertIn("Stale", self.auto.run(1)); self.assertEqual(writes, len(self.api.mutations))


if __name__ == "__main__": unittest.main()
