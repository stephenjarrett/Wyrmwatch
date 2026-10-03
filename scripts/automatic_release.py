"""Patch-version coordinator. Only trusted master orchestration may call this.

GitHub's repository token suppresses push/tag workflow triggers, so explicitly
dispatch read-only exact-head checks. Never rerun a failed run or update a tag.
"""
import json
import re
import xml.etree.ElementTree as ET

MARKER = "Wyrmwatch automatic patch release v1 "
PROPS = "Directory.Build.props"
HOST = "src/Wyrmwatch.Agent/ManagerHost.cs"
LANGUAGE = "src/Wyrmwatch.Desktop/Assets/Languages/en.json"


class AutomaticRelease:
    def __init__(self, handoff, policy):
        self.handoff, self.policy = handoff, policy
        self.manager, self.api, self.root = handoff.manager, handoff.api, handoff.root

    def replace_one(self, text, old, new):
        self.policy.require(text.count(old) == 1, "Version metadata is missing or ambiguous")
        return text.replace(old, new)

    def changes(self, base, old, version, run_id):
        """Deterministic four-file change; recovery verifies every byte and mode."""
        get = lambda path: self.handoff.source_text(path, base)
        props = get(PROPS)
        self.policy.require(ET.fromstring(props).findtext("PropertyGroup/Version") == old,
                            "Base version differs")
        files = {
            PROPS: self.replace_one(props, f"<Version>{old}</Version>", f"<Version>{version}</Version>"),
            HOST: self.replace_one(get(HOST), f'public const string Version = "{old}";',
                                   f'public const string Version = "{version}";'),
            LANGUAGE: self.replace_one(get(LANGUAGE), f'"v{old} · Preview"', f'"v{version} · Preview"'),
        }
        files[f"docs/releases/v{version}.md"] = (
            f"# Wyrmwatch {version}\n\n"
            f"Automatic patch release of verified master `{base}`. "
            f"[Source checks](https://github.com/{self.manager.repository}/actions/runs/{run_id}).\n\n"
            f"[Review changes](https://github.com/{self.manager.repository}/compare/v{old}...{base}). "
            "Includes matching source, license notices, SHA-256 files and the bundled .NET runtime. "
            "Windows installer and Linux packages remain preview builds. Automatic maintenance is opt-in. "
            "Structural fixtures do not certify real-world game compatibility. Keep independent world backups.\n"
        )
        return files

    def commit(self, version, verification):
        self.manager.ensure_release_allowed()
        self.policy.ensure_release_allowed(version)
        self.handoff.guard(verification)
        base, old = self.manager.head, self.manager.version
        files = self.changes(base, old, version, verification)
        parent = self.api.api(f"{self.root}/git/commits/{base}")
        tree = self.api.api(f"{self.root}/git/trees", "POST", {
            "base_tree": parent["tree"]["sha"],
            "tree": [dict(path=path, mode="100644", type="blob", content=content)
                     for path, content in sorted(files.items())],
        })
        proof = dict(base=base, old=old, version=version, run_id=verification)
        commit = self.api.api(f"{self.root}/git/commits", "POST", {
            "message": MARKER + json.dumps(proof, sort_keys=True), "tree": tree["sha"], "parents": [base],
        })
        self.policy.require(re.fullmatch(r"[0-9a-f]{40}", commit.get("sha", "")), "Invalid version commit")
        self.handoff.guard(verification)
        # A simultaneous user merge makes this sibling non-fast-forward. Never force.
        updated = self.api.api(f"{self.root}/git/refs/heads/master", "PATCH",
                               {"sha": commit["sha"], "force": False})
        self.policy.require(updated["object"]["sha"] == commit["sha"], "Version commit was not installed")
        return commit["sha"]

    def recover_version_commit(self):
        """Authorize a missing-check dispatch only for our exact metadata-only commit."""
        commit = self.api.api(f"{self.root}/git/commits/{self.manager.head}")
        message = commit.get("message", "")
        if not message.startswith(MARKER):
            return False
        proof = json.loads(message[len(MARKER):])
        self.policy.require(set(proof) == {"base", "old", "version", "run_id"}
                            and proof["version"] == self.manager.version
                            and re.fullmatch(r"[0-9a-f]{40}", proof["base"])
                            and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", proof["old"])
                            and type(proof["run_id"]) is int and proof["run_id"] > 0
                            and [p["sha"] for p in commit["parents"]] == [proof["base"]],
                            "Untrusted automatic version receipt")
        base_manager = self.policy.Manager(self.api, self.manager.repository, proof["old"], proof["base"], "Proof")
        base_manager.ensure_release_allowed()
        base_manager.checked_run(proof["run_id"])
        self.policy.require(tuple(map(int, proof["version"].split("."))) > tuple(map(int, proof["old"].split("."))),
                            "Automatic version must advance")
        files = self.changes(proof["base"], proof["old"], proof["version"], proof["run_id"])
        trees = []
        for sha in (proof["base"], self.manager.head):
            tree_sha = self.api.api(f"{self.root}/git/commits/{sha}")["tree"]["sha"]
            tree = self.api.api(f"{self.root}/git/trees/{tree_sha}?recursive=1")
            self.policy.require(not tree.get("truncated") and len(tree["tree"]) < 10000,
                                "Cannot verify complete metadata-only tree")
            trees.append({e["path"]: (e["mode"], e["type"], e["sha"]) for e in tree["tree"] if e["type"] != "tree"})
        changed = {p for p in trees[0].keys() | trees[1].keys() if trees[0].get(p) != trees[1].get(p)}
        self.policy.require(changed == set(files), "Version receipt includes unrelated changes")
        for path, content in files.items():
            self.policy.require(trees[1][path][:2] == ("100644", "blob")
                                and self.handoff.source_text(path, self.manager.head) == content,
                                "Version receipt differs from deterministic metadata")
        return True

    def dispatch(self, manager, packages=False, generated=False):
        manager.ensure_release_allowed()
        manager.current_master()
        # Include pending and failed runs: never duplicate or silently rerun them.
        if self.handoff.runs(manager, packages):
            return "Existing checks pending or unsuccessful; no automatic rerun"
        if not packages:
            self.policy.require(generated, "Only a verified own version commit may dispatch missing master checks")
        workflow = "release.yml" if packages else "build.yml"
        payload = {"ref": "master"}
        if packages:
            self.policy.require(manager.tag_commit() == manager.head, "Tag no longer matches current master")
            payload["inputs"] = {"approved_tag": manager.tag}
        self.api.api(f"{self.root}/actions/workflows/{workflow}/dispatches", "POST", payload)
        return "Dispatched exact-master " + ("tag package checks" if packages else "version checks")

    def next_version(self, releases, refs):
        current = tuple(map(int, self.manager.version.split(".")))
        reserved = {r.get("tag_name", "") for r in releases} | {r["ref"].removeprefix("refs/tags/") for r in refs}
        tree_sha = self.api.api(f"{self.root}/git/commits/{self.manager.head}")["tree"]["sha"]
        tree = self.api.api(f"{self.root}/git/trees/{tree_sha}?recursive=1")
        self.policy.require(not tree.get("truncated") and len(tree["tree"]) < 10000, "Cannot reserve existing release notes")
        for entry in tree["tree"]:
            match = re.fullmatch(r"docs/releases/(v[0-9]+\.[0-9]+\.[0-9]+)\.md", entry["path"])
            if match:
                reserved.add(match[1])
        # Never reuse a held/reserved patch, including unpublished or abandoned tags.
        reserved |= {"v" + v for v in json.loads(self.policy.HOLDS_FILE.read_text())["held_versions"]}
        patch = current[2] + 1
        while f"v{current[0]}.{current[1]}.{patch}" in reserved:
            patch += 1
            self.policy.require(patch - current[2] <= 1000, "Too many reserved versions")
        return f"{current[0]}.{current[1]}.{patch}"

    def run(self, event_run_id=None):
        self.manager.ensure_release_allowed()
        self.manager.current_master()
        if event_run_id is not None and self.handoff.trigger(event_run_id) is None:
            return "Stale or superseded event; no release changes"
        verification = self.handoff.discover(self.manager)
        if verification is None:
            if not self.handoff.runs(self.manager) and self.recover_version_commit():
                return self.dispatch(self.manager, generated=True)
            return "Waiting for successful checks at current master; no release changes"
        self.handoff.guard(verification)
        refs = self.api.api(f"{self.root}/git/matching-refs/tags/v")
        self.policy.require(len(refs) < 1000, "Too many tags for bounded version discovery")
        exact = [r for r in refs if r["ref"] == "refs/tags/" + self.manager.tag]
        self.policy.require(len(exact) <= 1, "Ambiguous immutable version tag")
        candidate = self.manager.find_release()
        metadata = self.policy.receipt(candidate.get("body"), self.manager.repository, self.manager.version) if candidate else None
        if metadata:
            self.policy.validate_assets(candidate, metadata, complete=metadata["phase"] != "preparing")
            self.policy.require(candidate["draft"] or metadata["phase"] == "published", "Unknown public candidate")
        used_elsewhere = (bool(exact) and self.manager.tag_commit() != self.manager.head) or (metadata and metadata["sha"] != self.manager.head)
        if used_elsewhere:
            version = self.next_version(self.api.api(f"{self.root}/releases?per_page=100"), refs)
            head = self.commit(version, verification)
            manager = self.policy.Manager(self.api, self.manager.repository, version, head, "Automatic version checks")
            return f"Advanced master to v{version} at {head}; " + self.dispatch(manager, generated=True)
        if candidate and not candidate["draft"]:
            return self.handoff.run()
        if not exact:
            self.manager.prepare(verification)
            # Tag creation is automatic under the user's merge-to-release policy.
            # Only a ready own draft with exact CI artifacts reaches this boundary.
            candidate = self.manager.find_release()
            metadata = self.policy.receipt(candidate["body"], self.manager.repository, self.manager.version)
            self.policy.require(candidate["draft"] and metadata["phase"] == "ready" and metadata["sha"] == self.manager.head,
                                "Only an exact verified ready draft can be tagged")
            self.policy.validate_assets(candidate, metadata)
            self.handoff.guard(verification)
            self.api.api(f"{self.root}/git/refs", "POST", {"ref": "refs/tags/" + self.manager.tag, "sha": self.manager.head})
            self.policy.require(self.manager.tag_commit() == self.manager.head, "Created tag does not match source")
        if not self.handoff.runs(self.manager, packages=True):
            return self.dispatch(self.manager, packages=True)
        return self.handoff.run(event_run_id)
