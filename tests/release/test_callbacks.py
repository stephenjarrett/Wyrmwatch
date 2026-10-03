"""Exercise real dispatch scripts and exact-run callback policy without network."""
import copy
import json
import os
import subprocess
import unittest
from unittest.mock import patch

import yaml
import test_release
import test_handoff
from test_handoff import handoff, policy, ROOT
import test_automatic
from test_automatic import automatic
from test_release import HEAD, VERSION, REPOSITORY


class CallbackWorkflowTests(unittest.TestCase):
    def workflow(self, name):
        return yaml.load((ROOT / ".github/workflows" / name).read_text(), Loader=yaml.BaseLoader)

    def execute(self, name, context=None, result="success", deny=False):
        job = self.workflow(name)["jobs"]["notify-handoff"]
        context = context or dict(ref="refs/heads/master", eventName="workflow_dispatch", runId=123,
                                  repo=dict(owner="fixture", repo="Wyrmwatch"))
        payload = dict(script=job["steps"][0]["with"]["script"], condition=job["if"],
                       context=context, result=result, dependency=job["needs"], deny=deny)
        # Execute the shipped JavaScript and job expression, not a Python replica.
        harness = r'''
const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const calls = [];
const github = {rest: {actions: {createWorkflowDispatch: async args => {
  calls.push(args); if (input.deny) throw new Error('Resource not accessible by integration');
}}}};
const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
(async () => {
  const enabled = new Function('github', 'needs', 'always', 'return (' + input.condition + ')')(
    {ref: input.context.ref, event_name: input.context.eventName},
    {[input.dependency]: {result: input.result}}, () => true);
  let error = null;
  if (enabled) {
    try { await new AsyncFunction('github', 'context', input.script)(github, input.context); }
    catch (e) { error = e.message; }
  }
  process.stdout.write(JSON.stringify({enabled, calls, error}));
})();
'''
        result = subprocess.run(["node", "-e", harness], input=json.dumps(payload), text=True,
                                capture_output=True, check=True)
        return json.loads(result.stdout)

    def test_notifications_have_only_actions_write_and_execute_no_repository_code(self):
        for name in ("build.yml", "release.yml"):
            with self.subTest(name=name):
                workflow = self.workflow(name)
                self.assertEqual({"contents": "read"}, workflow["permissions"])
                job = workflow["jobs"]["notify-handoff"]
                self.assertEqual({"actions": "write"}, job["permissions"])
                self.assertEqual(1, len(job["steps"]))
                self.assertEqual("actions/github-script@v8", job["steps"][0]["uses"])
                self.assertEqual("0", job["steps"][0]["with"]["retries"])
                self.assertNotIn("secrets.", job["steps"][0]["with"]["script"])

    def test_successful_master_push_and_dispatch_send_exact_run_callback(self):
        for name, events in (("build.yml", ("push", "workflow_dispatch")),
                             ("release.yml", ("workflow_dispatch",))):
            for event in events:
                with self.subTest(name=name, event=event):
                    context = dict(ref="refs/heads/master", eventName=event, runId=123,
                                   repo=dict(owner="fixture", repo="Wyrmwatch"))
                    outcome = self.execute(name, context)
                    self.assertIsNone(outcome["error"])
                    self.assertEqual([dict(owner="fixture", repo="Wyrmwatch", workflow_id="release-handoff.yml",
                                           ref="master", inputs={"completed_run_id": "123"})], outcome["calls"])

    def test_pr_feature_branch_tag_and_failed_source_never_dispatch(self):
        for name in ("build.yml", "release.yml"):
            for ref, event, result in (("refs/heads/master", "pull_request", "success"),
                                       ("refs/heads/sjarrett/fixture", "push", "success"),
                                       ("refs/tags/v0.2.4", "push", "success"),
                                       ("refs/heads/master", "workflow_dispatch", "cancelled"),
                                       ("refs/heads/master", "workflow_dispatch", "skipped")):
                with self.subTest(name=name, ref=ref, event=event, result=result):
                    context = dict(ref=ref, eventName=event, runId=123, repo={})
                    outcome = self.execute(name, context, result)
                    self.assertFalse(outcome["enabled"])
                    self.assertEqual([], outcome["calls"])
        self.assertEqual([], self.execute("build.yml", result="failure")["calls"])

    def test_failed_packages_notify_policy_and_api_denial_fails_visibly(self):
        self.assertEqual(1, len(self.execute("release.yml", result="failure")["calls"]))
        denied = self.execute("build.yml", deny=True)
        self.assertEqual(1, len(denied["calls"]))
        self.assertEqual("Resource not accessible by integration", denied["error"])

    def test_invalid_run_id_is_rejected_before_dispatch(self):
        for value in (0, -1, "123", 9007199254740992):
            context = dict(ref="refs/heads/master", eventName="workflow_dispatch", runId=value, repo={})
            outcome = self.execute("build.yml", context)
            self.assertEqual([], outcome["calls"])
            self.assertEqual("Invalid verification run ID", outcome["error"])


class CallbackPolicyTests(unittest.TestCase):
    def setUp(self):
        fixture = test_handoff.HandoffTests()
        fixture.setUp()
        self.api, self.manager, self.handoff = fixture.api, fixture.manager, fixture.handoff

    def test_callback_waits_for_same_run_to_finish_without_writes(self):
        self.api.runs[1].update(status="in_progress", conclusion=None)
        def complete(_):
            self.assertEqual([], self.api.mutations)
            self.api.runs[1].update(status="completed", conclusion="success")
        with patch.object(handoff.time, "sleep", side_effect=complete) as wait:
            self.assertEqual(1, self.handoff.await_callback("1"))
        wait.assert_called_once_with(5)
        self.assertEqual([], self.api.mutations)

    def test_timeout_is_bounded_and_never_reruns_or_writes(self):
        self.api.runs[1].update(status="in_progress", conclusion=None)
        with patch.object(handoff.time, "sleep") as wait:
            with self.assertRaisesRegex(policy.ReleaseError, "within two minutes"):
                self.handoff.await_callback("1")
        self.assertEqual(24, wait.call_count)
        self.assertEqual([], self.api.mutations)

    def test_invalid_input_is_rejected_before_api_reads(self):
        for value in ("0", "-1", " 1", "1 ", "01", "1e2", "1;ignored", "\uff11", "1" * 21):
            with self.subTest(value=value), patch.object(self.api, "api") as api:
                with self.assertRaises(policy.ReleaseError): self.handoff.await_callback(value)
                api.assert_not_called()

    def test_foreign_pr_wrong_workflow_branch_and_run_id_are_rejected(self):
        changes = ({"id": 7}, {"event": "pull_request"}, {"head_branch": "sjarrett/fixture"},
                   {"head_repository": {"full_name": "foreign/Wyrmwatch"}},
                   {"path": ".github/workflows/untrusted.yml"})
        for values in changes:
            with self.subTest(values=values):
                self.setUp()
                self.api.runs[1].update(values)
                with self.assertRaises(policy.ReleaseError): self.handoff.await_callback("1")
                self.assertEqual([], self.api.mutations)

    def test_stale_callback_does_nothing_and_master_advance_blocks_wait(self):
        self.api.runs[1]["head_sha"] = "b" * 40
        self.assertIsNone(self.handoff.await_callback("1"))
        self.assertEqual([], self.api.mutations)
        self.setUp()
        self.api.master = "b" * 40
        with self.assertRaisesRegex(policy.ReleaseError, "Master advanced"):
            self.handoff.await_callback("1")
        self.assertEqual([], self.api.mutations)

    def test_failed_source_abnormal_package_and_invalid_state_fail_closed(self):
        for values in ({"conclusion": "failure"}, {"status": "unknown", "conclusion": None},
                       {"path": ".github/workflows/release.yml", "event": "workflow_dispatch",
                        "conclusion": "cancelled"}):
            with self.subTest(values=values):
                self.setUp()
                self.api.runs[1].update(values)
                with self.assertRaises(policy.ReleaseError): self.handoff.await_callback("1")
                self.assertEqual([], self.api.mutations)

    def test_held_version_blocks_callback_before_api_access(self):
        manager = policy.Manager(self.api, REPOSITORY, "0.2.3", HEAD, "Held")
        with patch.object(self.api, "api") as api:
            with self.assertRaises(policy.ReleaseError): handoff.Handoff(manager).await_callback("1")
            api.assert_not_called()

    def test_permission_denial_is_not_retried(self):
        self.api.deny_api = True
        with patch.object(handoff.time, "sleep") as wait:
            with self.assertRaisesRegex(policy.ReleaseError, "Resource not accessible"):
                self.handoff.await_callback("1")
            wait.assert_not_called()
        self.assertEqual([], self.api.mutations)

    def test_cli_rejects_callback_on_wrong_event_and_malformed_id_before_checkout(self):
        for event, value in (("schedule", "1"), ("workflow_run", "1"), ("workflow_dispatch", "bad")):
            with self.subTest(event=event), patch.dict(os.environ, GITHUB_EVENT_NAME=event,
                    GITHUB_REF="refs/heads/master", COMPLETED_RUN_ID=value), \
                    patch.object(handoff.subprocess, "check_output") as git:
                with self.assertRaises(policy.ReleaseError): handoff.main()
                git.assert_not_called()


class CallbackReleaseTests(unittest.TestCase):
    def setUp(self):
        self.fixture = test_automatic.AutomaticTests()
        self.fixture.setUp()
        self.api, self.auto = self.fixture.api, self.fixture.auto

    def test_source_and_package_callbacks_publish_once_without_workflow_run_events(self):
        self.api.visible_runs = [1]
        source_id = self.auto.handoff.await_callback("1")
        self.assertIn("Dispatched", self.auto.run(source_id))
        self.api.runs[2].update(event="workflow_dispatch", head_branch="master")
        self.api.visible_runs.append(2)
        package_id = self.auto.handoff.await_callback("2")
        self.assertIn("Published verified", self.auto.run(package_id))
        published = copy.deepcopy(self.api.release)
        writes = len(self.api.mutations)
        for value in ("1", "2"):
            self.auto.run(self.auto.handoff.await_callback(value))
        self.assertEqual(writes, len(self.api.mutations))
        self.assertEqual(published, self.api.release)
        self.assertEqual(8, self.api.uploads)

    def published_repair(self):
        self.api.files[HEAD][".github/workflows/build.yml"] = "name: Checks\njobs:\n  build:\n    steps: []\n"
        auto, _ = self.fixture.repaired()
        auto.run(3)
        self.fixture.completed_repair_packages(auto)
        auto.run(4)
        newer = "c" * 40
        self.api.files[newer] = copy.deepcopy(self.api.files[auto.manager.head])
        self.api.files[newer][".github/workflows/build.yml"] += "  notify-handoff:\n    steps: []\n"
        self.api.commits[newer] = dict(message="Reviewed callback fix", parents=[{"sha": auto.manager.head}],
                                     tree={"sha": newer})
        self.api.master = self.api.verification_head = newer
        self.api.runs[3]["head_sha"] = newer
        manager = policy.Manager(self.api, REPOSITORY, VERSION, newer, "Notes")
        return automatic.AutomaticRelease(handoff.Handoff(manager), policy)

    def test_existing_build_steps_or_appended_other_jobs_are_not_callback_only(self):
        for fault in ("build", "other-job"):
            with self.subTest(fault=fault):
                self.setUp()
                auto = self.published_repair()
                name = ".github/workflows/build.yml"
                if fault == "build":
                    self.api.files[auto.manager.head][name] = self.api.files[auto.manager.head][name].replace(
                        "  build:", "  changed-build:")
                    self.assertFalse(auto.orchestration_only(HEAD))
                else:
                    self.api.files[auto.manager.head][name] += "  other-job:\n    steps: []\n"
                    with self.assertRaisesRegex(policy.ReleaseError, "final job"):
                        auto.orchestration_only(HEAD)

    def test_generated_version_checks_and_packages_progress_through_explicit_callbacks(self):
        auto = self.fixture.generated()
        self.api.runs[3].update(head_sha=self.api.new_head, event="workflow_dispatch")
        self.api.visible_runs = [3]
        version = auto.manager.version
        self.api.artifacts = []
        self.api.blobs = {}
        for artifact_id, runtime in enumerate(("win-x64", "linux-x64"), 1):
            files = {}
            for name in policy.expected_files(version, runtime):
                if not name.endswith(".sha256"):
                    files[name] = ("disposable " + name).encode()
                    files[name + ".sha256"] = f"{policy.sha(files[name])}  {name}\n".encode()
            blob = test_release.archive(files)
            self.api.blobs[artifact_id] = blob
            self.api.artifacts.append(dict(id=artifact_id, name="downloads-" + runtime, expired=False,
                                           size_in_bytes=len(blob), digest="sha256:" + policy.sha(blob)))
        original = self.api.api
        def versioned_api(path, method="GET", payload=None):
            suffix = path.removeprefix("repos/" + REPOSITORY)
            if suffix == "/git/matching-refs/tags/v" + version:
                return [r for r in self.api.refs if r["ref"] == "refs/tags/v" + version]
            if suffix == "/git/ref/tags/v" + version:
                return {"object": {"type": "commit", "sha": self.api.tag_sha}}
            if suffix == "/actions/runs/3/artifacts?per_page=100":
                return {"total_count": 2, "artifacts": self.api.artifacts}
            return original(path, method, payload)
        def versioned_upload(repository, tag, path):
            self.assertEqual(REPOSITORY, repository)
            self.assertEqual("v" + version, tag)
            self.assertTrue(self.api.release["draft"])
            self.api.uploads += 1
            self.api.release["assets"].append(dict(id=100 + self.api.uploads, name=path.name,
                                                  digest="sha256:" + policy.sha(path.read_bytes())))
        with patch.object(self.api, "api", side_effect=versioned_api), \
                patch.object(self.api, "upload", side_effect=versioned_upload):
            self.assertIn("Dispatched", auto.run(auto.handoff.await_callback("3")))
            run = copy.deepcopy(self.api.runs[2])
            run.update(id=4, head_sha=self.api.new_head, event="workflow_dispatch", head_branch="master",
                       display_title=f"Package v{version} at {self.api.new_head}")
            self.api.runs[4] = run
            self.api.visible_runs.append(4)
            self.assertIn("Published verified", auto.run(auto.handoff.await_callback("4")))
            snapshot = copy.deepcopy((self.api.release, self.api.refs, self.api.mutations))
            for value in ("3", "4"):
                auto.run(auto.handoff.await_callback(value))
            self.assertEqual(snapshot, (self.api.release, self.api.refs, self.api.mutations))
            self.assertEqual(8, self.api.uploads)
            self.assertEqual(1, sum(w[0] == "/git/refs/heads/master" for w in self.api.mutations))

    def test_merge_of_callback_only_fix_preserves_public_release_tag_and_receipt(self):
        auto = self.published_repair()
        snapshot = copy.deepcopy((self.api.release, self.api.refs, self.api.tag_sha, self.api.mutations))
        self.assertIn("requires no patch", auto.run(auto.handoff.await_callback("3")))
        self.assertEqual(snapshot, (self.api.release, self.api.refs, self.api.tag_sha, self.api.mutations))

    def test_application_version_packaging_and_hold_changes_require_normal_release_policy(self):
        for path in ("unchanged.cs", automatic.PROPS, "scripts/package-release.ps1",
                     "docs/releases/publication-holds.json"):
            with self.subTest(path=path):
                self.setUp()
                auto = self.published_repair()
                self.api.files[auto.manager.head][path] = "Application or release-input change"
                self.assertFalse(auto.orchestration_only(HEAD))

    def test_real_application_change_after_published_repair_advances_one_patch(self):
        auto = self.published_repair()
        self.api.files[auto.manager.head]["unchanged.cs"] += " real change"
        public = copy.deepcopy(self.api.release)
        self.assertIn("Advanced master to v0.2.5", auto.run(3))
        self.assertEqual(public, self.api.release)
        self.assertEqual(HEAD, self.api.tag_sha)

    def test_truncated_tree_cannot_skip_release_or_write_public_metadata(self):
        auto = self.published_repair()
        snapshot = copy.deepcopy(self.api.mutations)
        original = self.api.api
        def damaged(path, method="GET", payload=None):
            value = original(path, method, payload)
            if "/git/trees/" in path: value["truncated"] = True
            return value
        with patch.object(self.api, "api", side_effect=damaged):
            with self.assertRaisesRegex(policy.ReleaseError, "complete orchestration"):
                auto.run(3)
        self.assertEqual(snapshot, self.api.mutations)


if __name__ == "__main__":
    unittest.main()
