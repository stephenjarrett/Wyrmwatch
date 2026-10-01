"""Publication holds are durable local policy, including approved old candidates."""
import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from test_release import FakeGh, HEAD, REPOSITORY, VERSION, release
from test_handoff import HandoffGh, handoff


class PublicationHoldTests(unittest.TestCase):
    def test_held_version_blocks_every_writer_before_any_api_access(self):
        for mode in ("prepare", "publish", "recover", "handoff"):
            with self.subTest(mode=mode):
                api = FakeGh()
                manager = release.Manager(api, REPOSITORY, "0.2.3", HEAD, "Held candidate notes")
                with patch.object(api, "api", side_effect=AssertionError("Hold must precede API access")):
                    with self.assertRaisesRegex(release.ReleaseError, "v0.2.3.*held.*v0.2.4"):
                        if mode == "prepare": manager.prepare(1)
                        elif mode == "publish": manager.publish(2, "v0.2.3")
                        elif mode == "recover": manager.recover(2, "v0.2.3", 3, HEAD)
                        else: handoff.Handoff(manager).run()
                self.assertEqual([], api.mutations)
                self.assertEqual(0, api.uploads)

    def test_missing_malformed_or_ambiguous_hold_policy_fails_closed(self):
        valid = {"schema": 1, "held_versions": {}}
        documents = [None, "invalid json", json.dumps({"schema": 2, "held_versions": {}}),
                     '{"schema":1,"held_versions":{},"held_versions":{}}',
                     json.dumps({"schema": 1, "held_versions": []}),
                     json.dumps(dict(valid, unknown=True)),
                     json.dumps({"schema": 1, "held_versions": {"0.2.3": {"reason": "", "superseded_by": "0.2.4"}}}),
                     json.dumps({"schema": 1, "held_versions": {"0.2.3": {"reason": "Held", "superseded_by": "0.2.3"}}}),
                     json.dumps({"schema": 1, "held_versions": {"invalid": {"reason": "Held", "superseded_by": "0.2.4"}}}),
                     " " * (64 * 1024 + 1)]
        with tempfile.TemporaryDirectory(prefix="wyrmwatch-publication-hold-") as directory:
            path = Path(directory) / "holds.json"
            for document in documents:
                with self.subTest(document=document[:80] if document else "missing"):
                    path.unlink(missing_ok=True)
                    if document is not None: path.write_text(document, encoding="utf-8")
                    api = FakeGh()
                    manager = release.Manager(api, REPOSITORY, VERSION, HEAD, "Fixture notes")
                    with patch.object(release, "HOLDS_FILE", path), patch.object(api, "api") as calls:
                        with self.assertRaises(release.ReleaseError): manager.prepare(1)
                        calls.assert_not_called()
                    self.assertEqual([], api.mutations)

    def test_hold_is_rechecked_at_last_publication_boundary(self):
        api = FakeGh()
        manager = release.Manager(api, REPOSITORY, VERSION, HEAD, "Fixture notes")
        manager.prepare(1)
        before = copy.deepcopy(api.release)
        writes = len(api.mutations)
        with tempfile.TemporaryDirectory(prefix="wyrmwatch-publication-hold-") as directory:
            path = Path(directory) / "holds.json"
            path.write_text(json.dumps({"schema": 1, "held_versions": {}}), encoding="utf-8")
            def hold():
                path.write_text(json.dumps({"schema": 1, "held_versions": {
                    VERSION: {"reason": "New blocking finding", "superseded_by": "0.2.5"}}}), encoding="utf-8")
            with patch.object(release, "HOLDS_FILE", path):
                with self.assertRaisesRegex(release.ReleaseError, "held"):
                    manager.publish(2, "v" + VERSION, _before_publish=hold)
        self.assertEqual(writes, len(api.mutations))
        self.assertEqual(before, api.release)

    def test_new_candidate_preserves_superseded_untagged_draft_and_assets(self):
        api = FakeGh()
        manager = release.Manager(api, REPOSITORY, VERSION, HEAD, "Corrected candidate notes")
        manager.prepare(1)
        old = json.loads(json.dumps(api.release).replace(VERSION, "0.2.3"))
        old.update(id=11, tag_name="untagged-e544df8a98720da5ed9b", target_commitish="b" * 40)
        old["body"] = old["body"].replace(HEAD, "b" * 40)
        api.extra_releases.append(copy.deepcopy(old))
        api.release = None
        api.mutations.clear()
        manager.prepare(1)
        self.assertEqual(old, api.extra_releases[0])
        self.assertTrue(api.release["draft"])
        self.assertEqual("v0.2.4", api.release["tag_name"])
        self.assertFalse(any(path == "/releases/11" or path.startswith("/git/") for path, _, _ in api.mutations))

    def test_old_tag_completion_after_version_bump_is_read_only(self):
        api = HandoffGh()
        manager = release.Manager(api, REPOSITORY, VERSION, HEAD, "Corrected candidate notes")
        manager.prepare(1)
        old = json.loads(json.dumps(api.release).replace(VERSION, "0.2.3"))
        old.update(id=11, tag_name="untagged-e544df8a98720da5ed9b", target_commitish="b" * 40)
        old["body"] = old["body"].replace(HEAD, "b" * 40)
        api.extra_releases.append(copy.deepcopy(old))
        api.release = None
        api.mutations.clear()
        api.runs[2].update(head_branch="v0.2.3", head_sha="b" * 40)
        self.assertIn("superseded event", handoff.Handoff(manager).run(2))
        self.assertEqual([], api.mutations)
        self.assertEqual(old, api.extra_releases[0])
        self.assertIsNone(api.release)


if __name__ == "__main__":
    unittest.main()
