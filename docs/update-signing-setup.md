# Update signing setup — owner handoff required

Stephen approved dedicated update signing on condition that it does not cause
material bundle growth. Measure complete packages and installed payload against
0.2.7 before publication. The approved production key has now been generated. Test keys are
random, memory-only, and never printed. The owner must submit the private key to
GitHub personally; authorization to prepare setup does not authorize the agent
to transmit it.

Prepared local directory: `%LOCALAPPDATA%\WyrmwatchSigning`.
Its inherited permissions were disabled before generation and access is limited
to the current Windows account. The private file has never been read by the agent or
transmitted. The public key is embedded as `Assets/UpdateSigning.pub` with raw
public-key SHA-256 fingerprint
`F55BC01F275DBF16922F6796CAD810B8EBE448B68AB12C13EA9DE1513E042F52`.

**Remaining owner step:** manually open `NetSparkle_Ed25519.priv` in that private
directory and paste its contents into GitHub's **new repository secret** form,
named `WYRM_WATCH_UPDATE_SIGNING_KEY`. Submit it yourself, clear your clipboard,
and report only that it was added. The generation steps below document how the
prepared key was created; do not repeat them or replace this key.

## Owner steps after approval

1. Install the pinned official key-generation tool in a local tools directory:

   ```powershell
   dotnet tool install NetSparkleUpdater.Tools.AppCastGenerator --version 2.9.0 --tool-path .tools/netsparkle
   ```

2. Choose a private directory outside the repository, cloud-synced folders and
   build outputs. Create it with access limited to your Windows account. Run:

   ```powershell
   .tools/netsparkle/netsparkle-generate-appcast.exe --generate-keys --key-path <private-directory>
   ```

   This writes `NetSparkle_Ed25519.priv` (private signing seed) and
   `NetSparkle_Ed25519.pub` (public verification key). Do not run `--export`:
   it prints the private key. Do not send the private file or its contents in chat.

3. In GitHub's secure interface, open
   [Wyrmwatch Actions secrets](https://github.com/stephenjarrett/Wyrmwatch/settings/secrets/actions),
   choose **New repository secret**, and name it
   `WYRM_WATCH_UPDATE_SIGNING_KEY`. Paste the private file's base64 contents
   directly into GitHub's secret field yourself. Do not paste them into a shell
   command, issue, PR, workflow log or conversation. Keep an offline private
   backup for recovery and clear the clipboard after use.

4. Supply only `NetSparkle_Ed25519.pub` for review. The public key is safe to
   commit. The final implementation will embed it in the application, never
   trust a key supplied by the downloaded catalog, and refuse missing/invalid
   signatures. Key rotation needs a release trusted by the previous key.

5. Confirm the secret was added without revealing its value. The agent may
   check the secret's name and metadata, never retrieve or print its contents.
   Release signing must run only in the trusted release workflow, using its
   protected environment variable; pull-request jobs must not receive this key.

The signing job maps the repository secret to NetSparkle's
`SPARKLE_PRIVATE_KEY` environment variable and supplies the reviewed public key
as `SPARKLE_PUBLIC_KEY`. It signs a bounded catalog that binds version, immutable
source commit, artifact names, byte sizes and SHA-256 hashes. CI validates the
signature with the committed public key before publishing. It must fail closed
when either key or signature is absent. Never pass private material through
`--private-key-override`, command arguments, console export, build artifacts or
source files.

This is update authenticity signing, separate from Windows Authenticode. It
does not remove SmartScreen warnings or require changing Windows trusted roots.
No paid certificate, new machine privileges, or live app/server restart is part
of this handoff.

## Current implementation boundary

`SignedUpdateCatalog` and isolated tests implement strict verification and bind
the catalog to the official GitHub release digest/size, then recheck local file
bytes. They are a foundation only: the desktop action, replacement transaction,
legacy migration and release signing workflow are not wired yet. The production
public key is embedded and tested to reject randomly generated test signatures.
Do not describe this foundation as a completed updater.

## Upstream references

- [NetSparkle key generation and strict verification](https://github.com/NetSparkleUpdater/NetSparkle/tree/3.1.0#quick-start)
- [Pinned key-generation tool 2.9.0](https://www.nuget.org/packages/NetSparkleUpdater.Tools.AppCastGenerator/2.9.0)
- [Environment-key provider](https://github.com/NetSparkleUpdater/NetSparkle/blob/3.1.0/src/NetSparkle.Tools.AppCastGenerator/EnvironmentKeysProvider.cs)
