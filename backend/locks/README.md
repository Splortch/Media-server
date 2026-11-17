# Platform publish lockfiles

Self-contained publishing restores a runtime-specific NuGet graph. Ordinary project
lockfiles cover framework builds/tests; additional lockfiles cover linux-x64,
win-x64, osx-x64 and osx-arm64 publishing. Directory.Build.props selects them when
RuntimeIdentifier is set. Release and container publishing use locked mode.
scripts/check-release-locks.py requires the same reviewed framework package
versions and content hashes in every publish graph.

After an intentional production dependency update, run
`python3 scripts/refresh-nuget-locks.py`. This refreshes all consuming solution
projects, the benchmark outside the solution and all four runtime graphs, then
checks locked restore and the publish guard. Review and commit the diff.
Do not disable locked mode in the release workflow.

Same-repository Dependabot NuGet PRs receive automatic lockfile repair and fresh
validation runs. See [dependency updates](../../docs/dependency-updates.md).
