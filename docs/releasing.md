# Automatic verified releases

Successful **Checks and portable builds** runs at current `master` now drive the complete release sequence. No routine version edits, tag pushes or run-ID inputs are needed. Merging a reviewed change authorizes publication under this policy. v0.2.5 is the explicitly approved bootstrap release; subsequent changes normally increment the patch version. A deliberate major/minor version can still be supplied in a reviewed change with matching metadata and release notes.

1. Verify same-repository, exact-current-master checks on both platforms. PRs, forks, feature branches, failed checks and stale events cannot release.
2. If the source version was already used at another commit, reserve the next unused patch in its major/minor series. Existing release/tag versions and held versions are never reused. Preserve existing candidates and assets. Commit only `Directory.Build.props`, the agent version, the English preview version and deterministic release notes; update master with a non-forced fast-forward. A simultaneous user merge blocks this sibling update.
3. Explicitly dispatch read-only master checks for that new metadata commit. GitHub's normal repository token suppresses push/tag workflow triggers, so relying on those events would leave the release stalled. The version commit's exact source must pass all normal checks before its draft or tag can be prepared.
4. Download the two named source download artifacts as data. Validate bounded archives, GitHub archive digests, exact eight-file inventory and all SHA-256 checksums. Prepare only an owned private draft, reread its ready receipt and assets, and recheck current-master/hold policy.
5. Create the matching immutable lightweight version tag at that exact verified commit, using create-only API semantics. Never move or force-update a tag. Explicitly dispatch read-only `Release portable builds` from master with that tag. Each platform checks that the tag, project version and checkout SHA exactly match the workflow run's source SHA, then runs tests, bundled-runtime checks and installer/service checks.
6. Trusted current-master orchestration independently verifies the completed package run and original draft source/assets before publishing. Publication uses the verified source CI downloads, rather than replacing them with tag rebuild outputs. Public assets are never overwritten.

The normal runner `GITHUB_TOKEN` needs `contents: write` for version metadata, tags and owned releases, plus `actions: write` only in the handoff job for explicit dispatches. Package/build jobs retain read-only access; checkout credentials are not persisted. No PAT, new OAuth grant, credential extraction or alternate permission route is used. Branch protection or API permission refusals stop the workflow and require the normal owner-approved repository setting/action. The automation does not bypass those controls.

## Fail-closed recovery

The hourly scheduled handoff and no-input manual handoff discover current exact-head runs and durable receipts. Pending or failed checks are never automatically rerun. Permission failures are surfaced rather than masked. The `wyrmwatch-release` group serializes release writers with cancellation disabled. Read-only packages use a separate per-tag group, so scheduled handoffs cannot replace queued package checks. Dispatches return immediately instead of waiting under the writer lock.

If execution stops after committing the version but before dispatching checks, recovery requires the exact generated commit receipt, one parent with successful matching checks, and a complete nontruncated tree comparison. Only the four deterministic metadata files, their regular-file modes and every expected byte may differ. Unknown commits, changed application code, malformed receipts and unverified parents cannot dispatch missing checks. If execution stops after draft/tag creation but before package dispatch, the matching ready receipt, immutable tag and current master must still agree. Interrupted owned uploads stay private and can resume without adopting unknown drafts or replacing changed assets.

Repeated events, hourly recovery and a published release at the current source are no-ops. A subsequent real master change causes one new patch; the metadata commit itself proceeds through verification and publication rather than causing another bump. Ambiguous completed runs, duplicate/unknown candidates, altered receipts/assets, failed package jobs or unrelated failed guards block publication. Only the prior legacy publisher-only failure can be recovered after both mandatory package jobs passed. Stale tag completions cannot publish over a newer source.

`docs/releases/publication-holds.json` remains the trusted fail-closed supersession gate. Missing, malformed or ambiguous policy blocks release writes and is rechecked immediately before publication. **v0.2.3 remains held**: preserve its existing immutable tag, private draft and assets. It must not be published, adopted or reused; corrected v0.2.4 and newer candidates have their own sources/assets.

Actions summaries state what was committed/dispatched/prepared/published, or which checks are still required. Fix failed checks in a normal reviewed change; do not silently rerun them or edit managed draft assets during preparation. Automatic verification covers disposable fixtures and package integrity, not real Dragonwilds world compatibility.

## Local checks

```text
python -m pip install -r tests/release/requirements.txt
python -m unittest discover -s tests/release -v
```

The Desktop test assembly remains serial because Avalonia and `Program` have process-wide state. Its isolated cases use unique temporary workspaces and restore process flags afterward. Independent test processes can use separate screenshot output directories.
