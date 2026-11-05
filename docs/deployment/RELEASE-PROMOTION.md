# GitHub release approval: Alpha, Beta and Stable

Automatic Alpha builds are unchanged: the existing release pipeline publishes
platform prereleases after the required main checks pass. Promotion only assigns
a new GitHub tag and release classification to an already released commit.

| Status | Example | GitHub classification |
| --- | --- | --- |
| Alpha | `server-windows/v0.2.0-alpha.284` | Prerelease, automatic |
| Beta | `server-windows/v0.2.0-beta.1` | Prerelease, maintainer approval |
| Stable | `server-windows/v0.2.0` | Normal release, maintainer approval |

The repository publishes each platform separately. Keep the source platform
prefix (`server-windows`, `server-linux`, `server-macos`, `client-windows`,
`client-linux`, `client-macos`, `client-android`, or `docker`). Client versions
currently use base `0.1.0`; server versions use `0.2.0`. Unprefixed tags such as
`v0.2.0-beta.1` are accepted only when the source release is also unprefixed.

## Promote an existing build

1. In GitHub Releases, select the published Alpha or Beta for the platform you
   have tested. Copy its full tag. This can be an older commit; it does not need
   to be the current main head.
2. Open **Actions → Promote existing GitHub release → Run workflow**, select
   **main**, and enter the source tag and desired target tag. For example,
   promote `server-windows/v0.2.0-alpha.284` to
   `server-windows/v0.2.0-beta.1`, then promote that Beta to
   `server-windows/v0.2.0` after approval. Alpha can also be promoted directly to
   Stable. Repeat per platform as needed.
3. Check the workflow summary for the resulting release. Beta is a prerelease;
   Stable is a normal release. The workflow does not change GitHub's Latest
   selection, publish container aliases, or deploy software.

The target must keep the platform and base version, and be newer than all
existing tags for that platform. Counters are numeric: Beta 10 follows Beta 9.
Leading zeros, duplicate tags, backward promotions and build-metadata suffixes
are rejected. Once a Stable tag exists, it cannot be replaced by another build.
A future base-version change belongs to the normal version planning process.

## Downloads and exact source

The new release description links directly to the original platform assets and
lists their GitHub SHA-256 digests. They are **not copied, renamed, replaced or
rebuilt**. Use the **Existing downloads** links in the description; the promoted
release does not contain duplicate attachments. GitHub's generated source
archives refer to the exact promoted commit. Original Alpha releases must stay
published so those download links remain available.

Installers, embedded application versions, filenames, `release.json`,
`SHA256SUMS`, Docker tags, and download formats keep their original build
identity. Beta/Stable describes the official approval/testing status of those
same bytes; it does not create another app edition or an update channel. Later
promotion of a Beta keeps links to the original Alpha downloads.

The workflow resolves lightweight or annotated source tags to a commit, checks
that the release records that exact SHA, and requires the latest attempts of all
existing required main CI workflows to have passed on that SHA. Android also
requires its own exact-commit Android workflow. The source must have published
assets, a manifest, checksums and GitHub SHA-256 digests. Missing/expired CI
history or missing digests fail closed; do not bypass the check by selecting
newer main code or relabeling unverified files.

Only a manual dispatch on main may write. PR runs only execute tests. Inputs go
through environment variables, never shell interpolation. The workflow never
checks out or runs code from the selected release commit.

Duplicate targets are rejected atomically. Failed publication can leave a tag
or draft; it never overwrites a published release. Inspect the run and source
SHA before manually removing an unpublished draft/tag and retrying. Do not move
or delete published tags to reuse a version.
