"""Offline pagination and provenance regressions for long-lived master revisions."""
import copy
import unittest
from unittest.mock import patch

from test_handoff import HandoffGh, handoff, policy
from test_release import HEAD, REPOSITORY, VERSION


class RunDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.api = HandoffGh()
        self.manager = policy.Manager(self.api, REPOSITORY, VERSION, HEAD, "# Notes")
        self.handoff = handoff.Handoff(self.manager)

    def prepend_runs(self, count, **changes):
        ids = list(range(100, 100 + count))
        for run_id in ids:
            self.api.runs[run_id] = dict(copy.deepcopy(self.api.runs[1]), id=run_id, **changes)
        self.api.visible_runs = ids + self.api.visible_runs
        return ids

    def test_hourly_handoffs_cannot_bury_source_checks_on_unchanged_master(self):
        self.prepend_runs(1200, event="schedule", path=".github/workflows/release-handoff.yml")
        with patch.object(self.api, "api", wraps=self.api.api) as api:
            self.assertEqual(1, self.handoff.discover(self.manager))
        listings = [call.args[0] for call in api.call_args_list if "/runs?" in call.args[0]]
        self.assertEqual([f"repos/{REPOSITORY}/actions/workflows/build.yml/runs?branch=master"
                          f"&head_sha={HEAD}&per_page=100&page=1"], listings)
        self.assertEqual([], self.api.mutations)

    def test_source_checks_on_third_page_keep_full_provenance_validation(self):
        self.prepend_runs(205, event="pull_request")
        with patch.object(self.api, "api", wraps=self.api.api) as api:
            self.assertEqual(1, self.handoff.discover(self.manager))
        pages = [call.args[0].rsplit("=", 1)[1] for call in api.call_args_list if "/runs?" in call.args[0]]
        self.assertEqual(["1", "2", "3"], pages)
        self.assertEqual([], self.api.mutations)

    def test_ambiguous_successful_source_on_later_page_still_blocks(self):
        self.prepend_runs(100, event="pull_request")
        self.api.visible_runs = [1] + [r for r in self.api.visible_runs if r != 1] + [3]
        with self.assertRaisesRegex(policy.ReleaseError, "Multiple matching"):
            self.handoff.run()
        self.assertEqual([], self.api.mutations)

    def test_pending_and_failed_checks_on_later_pages_are_not_lost(self):
        for status, conclusion in (("queued", None), ("in_progress", None), ("completed", "failure")):
            with self.subTest(status=status):
                self.setUp()
                self.prepend_runs(100, event="pull_request")
                self.api.runs[1].update(status=status, conclusion=conclusion)
                self.assertEqual([1], [r["id"] for r in self.handoff.runs(self.manager)])
                self.assertIsNone(self.handoff.discover(self.manager))
                self.assertEqual([], self.api.mutations)

    def test_package_discovery_reads_tag_and_repaired_control_cohorts(self):
        control = "b" * 40
        manager = policy.Manager(self.api, REPOSITORY, VERSION, HEAD, "# Notes", control_head=control)
        self.api.visible_runs = [2]
        self.api.runs[4] = dict(copy.deepcopy(self.api.runs[2]), id=4, event="workflow_dispatch",
                                head_branch="master", head_sha=control, display_title=manager.package_title())
        self.api.visible_runs.append(4)
        self.prepend_runs(100, event="pull_request", path=".github/workflows/release.yml",
                          head_branch="master", head_sha=control)
        self.assertEqual({2, 4}, {r["id"] for r in self.handoff.runs(manager, packages=True)})
        with self.assertRaisesRegex(policy.ReleaseError, "Multiple matching"):
            self.handoff.discover(manager, packages=True)
        self.assertEqual([], self.api.mutations)

    def test_empty_and_exact_page_boundary_inventories(self):
        for count in (0, 100, 200):
            with self.subTest(count=count):
                self.setUp()
                self.api.visible_runs = []
                self.prepend_runs(count, event="pull_request")
                with patch.object(self.api, "api", wraps=self.api.api) as api:
                    self.assertEqual([], self.handoff.runs(self.manager))
                self.assertEqual(max(1, count // 100), api.call_count)
                self.assertEqual([], self.api.mutations)

    def test_truncated_duplicate_changing_or_capped_pages_fail_closed(self):
        for fault in ("short", "duplicate", "changed", "ceiling", "invalid_count"):
            with self.subTest(fault=fault):
                self.setUp()
                self.prepend_runs(100, event="pull_request")
                original = self.api.api
                def broken(path, method="GET", payload=None):
                    result = original(path, method, payload)
                    if "/runs?" in path:
                        if fault == "ceiling":
                            result["total_count"] = 1000
                        elif fault == "invalid_count":
                            result["total_count"] = -1
                        elif path.endswith("page=2"):
                            if fault == "short": result["workflow_runs"] = []
                            elif fault == "duplicate": result["workflow_runs"][0] = copy.deepcopy(self.api.runs[100])
                            elif fault == "changed": result["total_count"] += 1
                    return result
                with patch.object(self.api, "api", side_effect=broken):
                    with self.assertRaises(policy.ReleaseError): self.handoff.run()
                self.assertEqual([], self.api.mutations)

    def test_permission_denial_on_later_page_is_not_retried_or_masked(self):
        self.prepend_runs(100, event="pull_request")
        original = self.api.api
        def denied(path, method="GET", payload=None):
            if path.endswith("page=2"):
                raise policy.ReleaseError("Resource not accessible by integration")
            return original(path, method, payload)
        with patch.object(self.api, "api", side_effect=denied) as api:
            with self.assertRaisesRegex(policy.ReleaseError, "Resource not accessible"):
                self.handoff.run()
        self.assertEqual(1, sum(call.args[0].endswith("page=2") for call in api.call_args_list))
        self.assertEqual([], self.api.mutations)


if __name__ == "__main__":
    unittest.main()
