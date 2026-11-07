# GitHub release promotion: Alpha, Beta and Stable

Every validated main commit publishes one combined Alpha prerelease with the
available Windows, Linux, macOS, Android, desktop-client and Docker downloads.
Promoting a release creates a new GitHub release entry for the same exact
commit. It reuses the tested Alpha downloads; it does not rebuild or relabel
application binaries.

| Stage | Example tag | GitHub status |
| --- | --- | --- |
| Alpha | `v0.2.0-alpha.284` | Automatic prerelease |
| Beta | `v0.2.0-beta.1` | Maintainer-approved prerelease |
| Stable | `v0.2.0` | Maintainer-approved normal release |

The tag belongs to the combined release, so promote it once for all platforms.
Existing platform-specific releases remain available as historical releases.

## Promote a combined release

1. In GitHub Releases, choose the published combined Alpha you tested and copy
   its full tag, for example `v0.2.0-alpha.284`.
2. Open **Actions → Promote existing GitHub release → Run workflow**, select
   **main**, and enter the source and target tags. For Beta, use
   `v0.2.0-beta.1`. After Beta approval, promote that tag to `v0.2.0`.
   Alpha can also be promoted directly to Stable.
3. Review the workflow summary and the resulting release description. It lists
   the original downloads for every platform and links to the original
   checksums. Stable is a normal GitHub release; Alpha and Beta remain marked
   as prereleases.

Promotion is manual and only runs from `main`. It requires all required main
CI workflows and the Android workflow to have passed on the exact commit. The
workflow verifies the tag's commit, complete combined asset set, manifest,
checksums and GitHub asset digests before creating the new release.

## What promotion changes

The new Beta or Stable release entry points to the original Alpha assets
byte-for-byte. It does not copy or rebuild packages, change embedded component
versions, edit checksums, push a Docker alias, or deploy software. The
downloads retain their Alpha build identity; the release status communicates
that maintainers approved that exact tested build.

When promoting Beta to Stable, the stable description continues linking to the
original Alpha downloads and checksums. Keep the original Alpha release
published so those links continue to work.

## Version and tag rules

Promotion stays within the same base version and advances maturity:
Alpha → Beta or Stable; Beta → a later Beta or Stable. Beta counters are
numeric, so Beta 10 follows Beta 9. Duplicate targets, older versions, leading
zeroes and build-metadata suffixes are rejected. Published tags are never
moved or overwritten.

The workflow creates a draft first and publishes it only after rechecking the
source tags. If publication fails, inspect the run and draft before retrying.
Do not delete or move a published tag to reuse its version.

## Historical platform releases

Older releases may use platform-prefixed tags such as
`server-windows/v0.2.0-alpha.284`. Those can still be promoted using the same
platform prefix on the target tag. New combined releases use the unprefixed
tags shown above and need only one promotion for all platform downloads.
