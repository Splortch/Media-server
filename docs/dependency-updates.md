# Dependency updates

Dependabot proposes weekly updates after a seven-day release cooldown for NuGet,
npm, Cargo and Gradle. Security updates are not delayed by that version-update
cooldown. Related .NET, React, Tailwind, lint, build/test, Tauri and Android Media3
packages are grouped. Shared npm/Cargo dependencies update across client
directories together. Unrelated major upgrades remain separate proposals.

.NET platform packages remain on their current major, matching the net10.0 target.
Docker stays within the existing major/minor image series; tags and digests remain
pinned. Major Android/Kotlin plugin upgrades require an explicit migration.
Node type-definition major upgrades also require review against CI's Node 24 runtime;
the existing Node 26 definitions are not downgraded by this policy.

## Automatic NuGet lock repair

Dependabot can update a leaf project's lockfile without updating its consumers or
the publish RID graphs. This produces NU1004 before tests start.

1. Dependabot lock preparation runs on same-repository Dependabot NuGet PRs with
   a read-only token, no saved Git credentials and no secrets.
2. It restores the entire solution, tools outside the solution, and the four
   publish graphs (Windows, Linux and both macOS architectures).
3. It verifies locked solution restore and the existing publish graph guard,
   then uploads a JSON bundle of changed lockfiles.
4. A separate workflow_run job executes only default-branch code. It verifies
   the bot identity, original PR head, repository, branch and changed file paths.
   It accepts only existing regular NuGet lockfiles, creates a non-forced commit,
   and explicitly dispatches all eight validation workflows against the repaired
   branch. It never executes code from the downloaded artifact or PR.

Normal build, test, audit, container and release checks keep locked mode.
An initial PR run may report stale locks while repair is running; fresh checks
run on the repair commit. Genuine dependency/API failures still need a fix.
The publisher never merges PRs, disables checks or writes to main.

The automation becomes active after these workflows are merged into main.
For older NuGet PRs, rebase them through Dependabot to trigger preparation on a
current branch containing the scripts. Conflicts must be resolved first.
Non-NuGet lockfile changes still require their package manager's normal review.

## Local repair

Run:

```sh
python3 scripts/refresh-nuget-locks.py
git diff -- backend
```

Review and commit the changed lockfiles, then run the regular component checks.
If automatic publishing succeeds but workflow dispatch fails, manually run the
listed workflows against that Dependabot branch from the Actions page.
