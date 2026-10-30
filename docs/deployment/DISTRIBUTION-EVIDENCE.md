# Distribution evidence (#2)

The native server packaging job generates these files **before** archiving or
compiling installers, so Windows setup.exe, Linux DEB/tar and macOS PKG/tar contain
identical evidence for their respective source/runtime:

- `inventory/SBOM.spdx.json`: SPDX 2.3 package metadata and locked archive hashes.
- `inventory/THIRD-PARTY-INVENTORY.json`: declared dependency license metadata,
  evidence filenames, source commit, target runtime and coverage limitations.
- `inventory/license-evidence/`: bounded copies of available license and notice
  files; no dependency code is imported or executed by the generator.

NuGet metadata comes from the exact restored package graph and local nuspecs.
Browser metadata comes from the committed npm lock and installed production
packages; development-only npm packages are excluded. A missing/ambiguous NuGet
package or mismatched browser identity/version fails publication rather than
emitting invented evidence. Package metadata and license file sizes are bounded;
license paths must remain within restored package roots. Absolute cache paths do
not appear in the output. Start with an empty output directory; stale evidence
causes a failure and is never silently reused or deleted.

`licenseDeclared` and `licenseConcluded` intentionally remain `NOASSERTION` in
SPDX. Upstream declarations are preserved as **unreviewed metadata** in the report
and comments. This report is evidence for maintainers to review, not a statement
that redistribution obligations are fulfilled. Lock hashes identify downloaded
package archives; they do not assert that installed files are byte-identical to
the archive. The intentionally fixed epoch creation timestamp makes inventory
bytes repeatable for the same source, runtime and restored graph; the source
commit identifies the build, not that timestamp.

## Remaining acceptance work

Issue #2 remains open for a full distribution review:

- Inventory Microsoft framework/runtime packs and their upstream third-party
  notices, not only the NuGet restore graph and browser runtime dependencies.
- Add equivalent Android, Rust/Tauri and WebView/native SDK dependency inventories.
- Review every redistributed license, notice and corresponding-source obligation.
- Retain the container's exact installed package list, FFmpeg license/build output
  and corresponding-source bundle; these already have a separate release path.
- Perform clean physical-machine install and rollback checks; hosted installer
  tests are automated runner evidence, not personal-device certification.
- Establish artifact signing. Checksums detect corruption, not publisher identity.

The existing base-image digest pins, locked dependency restores, unsigned-alpha
labels, actual installer smoke tests and rollback guidance remain in place.
The whole build is not claimed bit-for-bit reproducible: runner images, apt inputs,
platform toolchains and packaging timestamps can still vary.
