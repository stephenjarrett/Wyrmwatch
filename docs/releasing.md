# Preparing and publishing a release

Successful **Checks and portable builds** push runs at current `master` automatically prepare a private draft for the version in `Directory.Build.props`. Add `docs/releases/vVERSION.md` before merging a candidate. PRs, forks, feature branches, failed checks and stale events cannot prepare or publish it. No automatic version bump or tag creation occurs.

Approve publication by pushing the matching `vVERSION` tag at the prepared candidate's exact commit. The tag remains the publication gate: merging without an approved tag only prepares a draft. **Prepare and publish approved releases** automatically discovers the successful master checks, source receipt and matching tag package run. There are no run-ID inputs or routine manual release steps. It reacts to either master checks or tag packaging completing, so tag-before-draft and draft-before-tag ordering both work. A tag completion can discover and prepare an already-checked source even if a pending master event was coalesced.

The tag workflow has read-only repository access and runs both platform tests, portable builds, packaged-runtime checks and installer/service checks. Publication orchestration always comes from trusted current master, requires successful exact-head master checks before writes, and independently validates the triggering event against GitHub's run API. It never executes code from a tag checkout, PR or downloaded artifact with release write access. Only the handoff job has `contents: write` and `actions: read`; checkouts do not persist credentials. It does not rerun failed CI or request additional credentials/permissions.

Draft preparation downloads only the two named source download artifacts. It checks the GitHub artifact archive digests, exact bounded archive contents, all package checksums and the eight release-asset digests. The ownership receipt pins the repository, version, source commit, successful master run and asset inventory. Readiness updates explicitly preserve the version tag and source, then reread and validate the final ready state. Unknown drafts, duplicate candidates, changed receipts and incomplete or altered assets fail closed. Interrupted own uploads remain unpublished and can resume safely.

When an approved tag points to an older checked commit of the same current version, the handoff preserves and publishes that exact candidate's original downloads. Source version and release notes are read as bounded metadata pinned to the approved commit. If its draft is missing, the original successful master run and artifacts must be discoverable before a draft can be prepared; an existing candidate with a different source is never replaced to fit the tag. A verified own `untagged-*` draft can be recovered through its matching ownership receipt. A historical tag run whose package jobs passed but legacy publisher failed can be recovered automatically; failed packages or any unrelated failed guard remain blocking. This includes the pending approved v0.2.3 candidate without moving its tag.

The handoff rediscoveries are bounded and reject ambiguous completed push runs instead of choosing arbitrary results. Fresh master/tag/receipt checks before publication stop changed or superseded candidates; a newer public version cannot be displaced. Publication uses the already-verified source downloads rather than tag rebuild outputs. Repeated matching events and already-published managed releases are no-ops; public downloads are never overwritten. Tag jobs and the handoff share `wyrmwatch-release` concurrency with cancellation disabled. Do not manually alter a managed draft during preparation or move/force-update an approved tag.

Each handoff writes an Actions summary stating whether it prepared a draft, published the approved candidate, made no changes, or is waiting for named checks/approval. If GitHub event delivery needs a manual recovery, an authorized maintainer may run the same handoff from `master` with no inputs; it performs identical automatic discovery and safety gates. Any API permission refusal stops the workflow and requires the normal authorized access to be restored; no alternate credential route is used.

Run offline policy/fault tests with:

```text
python -m pip install -r tests/release/requirements.txt
python -m unittest discover -s tests/release -v
```

The Desktop test assembly remains serial within each process because Avalonia and `Program` have process-wide state. Test classes derive from `IsolatedDesktopTest`, which resets normal/headless mode and a unique temporary workspace for each case, then restores all flags and the prior workspace even when a test fails. Independent test processes can run concurrently with separate screenshot output directories.
