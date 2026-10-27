# Platform publish lockfiles

Self-contained publishing restores a runtime-specific NuGet graph. The ordinary project
lockfiles cover framework builds/tests; these additional lockfiles cover linux-x64 and
win-x64 publishing. Directory.Build.props selects them only when RuntimeIdentifier is set.
Release and container publishing use locked mode. `scripts/check-release-locks.py` requires
the same reviewed framework package versions and content hashes in all three graphs.

After an intentional production dependency update, copy each project's ordinary lockfile
to its two runtime directories, restore Api.csproj once for each runtime with
`dotnet restore backend/src/Api/Api.csproj -p:RuntimeIdentifier=RID -p:RestoreLockedMode=false`, review the diff,
then run the guard and locked publishing. Do not disable locked mode in the release workflow.
