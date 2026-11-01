# Release channels and promotion

All Splortch Media Server releases are public. Releases are separated by maturity, not by who may download them.

| Channel | Example server version | Trigger | GitHub type |
| --- | --- | --- | --- |
| Alpha | `v0.2.0-alpha.123` | Required CI succeeds for latest `main` | Prerelease |
| Beta | `v0.2.0-beta.1` | Maintainer manually promotes an approved Alpha commit | Prerelease |
| Stable | `v0.2.0` | Maintainer manually promotes an approved Beta commit | Normal release |

## How to promote
1. Open **Actions → Automatic prereleases → Run workflow** on `main`.
2. For **Beta** select `channel=beta`, enter a complete source server tag such as `v0.2.0-alpha.123`, and provide a new `beta_number` such as `1`.
3. For **Stable** select `channel=stable`, and use the **approved Beta** server tag such as `v0.2.0-beta.1` as `source_tag`.
4. Check **publish** to acknowledge release publication, then run the workflow.
5. Verify all platform releases and SHA256SUMS after the run finishes. Do not deploy to an existing family server without backing up its data.

Promotion verifies that the selected **source release family** exists and points to a single immutable Git commit. The build jobs check out that commit rather than an unrelated later `main` commit. Never use a prerelease from before channel support was added: the selected commit must contain the updated version stamping and packaging scripts.

The workflow rebuilds installers for each promoted channel. It does not rename Alpha archives and pretend they are Beta/Stable. Public release artifacts remain separate for Windows, Linux, macOS, Docker and other supported components.

A failed build must be investigated; do not manually override failed publication guards. Note that the GitHub Actions manual workflow permission is governed by repository write permissions. Limit who has write rights and require repository environment protection when broader team approval is required.

## Upgrades and downgrades
Alpha/Beta versions are intended for testing. Back up configuration, account data, and the database before installation. A rollback of application binaries may not revert schema changes. Release channel selection happens only when maintainers publish GitHub releases; this feature does not add application settings or change the application's update UI.
