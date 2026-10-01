# Preparing and publishing a release

Successful **Checks and portable builds** runs for a push to `master` prepare a private draft for the version in `Directory.Build.props`. PRs, forks, feature branches, failures and stale master commits cannot prepare it. Add `docs/releases/vVERSION.md` before merging a release candidate. No automatic version bump or public release occurs on merge.

Draft preparation verifies both platform jobs, downloads only their named download artifacts, requires the GitHub artifact archive digest to match, rejects unexpected archive paths and checks every package's accompanying SHA-256 file. The downloaded data is never executed by the job with release write access. The draft records its source commit, successful master run and full asset digest inventory. Only releases with this workflow's ownership receipt can be resumed or updated; unknown drafts/assets and altered files are refused. A failed upload leaves the draft visibly incomplete and unpublishable.

Approve publication by pushing the matching `vVERSION` tag **at the draft's exact recorded commit**, after its draft preparation succeeds. The existing tag workflow reruns tests, both platform builds, packaged-runtime checks and installer/service checks. Its publisher then verifies the tag, successful master run, ready receipt and all eight uploaded assets before publishing the prepared draft as latest. It publishes the already-verified master downloads; it does not replace them with a second set of rebuild outputs. Tag-build artifacts remain available for inspection. Repeated publication of an unchanged managed release is a no-op; published releases are never overwritten.

Both release workflows share `wyrmwatch-release` concurrency with cancellation disabled. Stale master work fails closed and a later successful run can prepare the current draft. Do not publish or alter the managed draft manually during preparation: publication uses the approved tag gate. If master advances during an upload, preparation leaves the draft incomplete. If preparation or tag publication fails, inspect the actual error and rerun the corresponding workflow using an authorized GitHub session after correcting the cause. Do not move or force-update an approved tag.

The build jobs have read-only repository access. Only draft preparation/publication jobs have `contents: write` and `actions: read`; checkouts do not persist their credentials. Those jobs run only trusted repository master/tag code. They do not run PR artifact code, change installations or deploy the application.

Run offline policy/fault tests with:

```text
python -m pip install -r tests/release/requirements.txt
python -m unittest discover -s tests/release -v
```

The Desktop test assembly remains serial within each process because Avalonia and `Program` have process-wide state. Test classes derive from `IsolatedDesktopTest`, which resets normal/headless mode and a unique temporary workspace for each case, then restores all flags and the prior workspace even when a test fails. Independent test processes can run concurrently with separate screenshot output directories.
