# Main branch ruleset

The import-ready policy is [main.json](../../.github/rulesets/main.json).
**Committing this file does not activate GitHub protection.** A repository administrator must import it once; GitHub stores the active ruleset separately. Later edits to this file also require updating the active ruleset.

## Activate in GitHub

1. Download the raw `main.json` file from this repository after reviewing the policy.
2. Open [repository rulesets](https://github.com/Splortch/Media-server/settings/rules).
3. Choose **New ruleset → Import a ruleset** and select the downloaded JSON file.
4. Confirm the name is **Main branch protection**, enforcement is **Active**, and the target is only `main`.
5. Review the required checks and save the ruleset. Open it again to confirm the saved policy matches this file.
6. On a normal feature PR, confirm merging is blocked while required checks are pending or failing and becomes available after they pass and conversations are resolved. Do not test force pushes or deletion against the live branch.

Alternatively, with an administrator-authenticated GitHub CLI, run from the repository root:

```sh
gh api --method POST repos/Splortch/Media-server/rulesets --input .github/rulesets/main.json
```

This command creates a ruleset: run it only once. Inspect existing rulesets first with `gh api repos/Splortch/Media-server/rulesets`; update the existing rule through Settings rather than creating duplicates. Never put administrator tokens in the repository or workflows.

## Policy for two maintainers

- All changes to `main` go through a pull request, including administrator and automation changes. There are no bypass actors.
- Required checks must pass on a branch up to date with `main`. Updating the branch can rerun CI.
- Review conversations must be resolved. The required approval count is zero so either maintainer can finish a passing PR when the other is unavailable. Reviews are still encouraged for security and architecture changes.
- Force pushes and deletion of `main` are blocked.
- Feature branches, release tags, and the generated downloads branch are outside this ruleset. Existing release workflows can continue creating their artifacts and catalog updates.
- No mandatory signed commits, linear history, deployment gates, or merge queue are introduced.

## Required checks

All contexts below were verified on PR #100 and are bound to the GitHub Actions app (integration ID `15368`). Their workflows run on every pull request. Component filtering happens inside jobs, allowing unaffected PRs to report success.

| Check | Coverage |
| --- | --- |
| Backend (ubuntu-24.04) | Linux backend build and tests |
| Backend (windows-latest) | Windows backend build and tests |
| Frontend | Web checks |
| End-to-end | Browser/backend integration |
| Docker build | Container build |
| Agent dispatch policy | Aggregate automation tests on Linux and Windows |
| Windows client | Native Windows client validation |
| Rust dependency audit | Rust dependency advisories |
| GLib optimized regression | Native dependency regression |
| Desktop packages | Aggregate Linux and macOS desktop packaging |

Android and release workflows have path/event filters and therefore are not globally required: a missing check would otherwise block unrelated PRs. This ruleset does not bypass their failures or change their workflows. CodeQL remains enabled independently; it is not added as a new global gate here.

When renaming a required job or changing workflow triggers, update both this file's JSON policy and the active GitHub ruleset in the same maintenance window. A stale check name can block every PR. An administrator can edit or temporarily disable the ruleset in Settings to recover, then restore protection after repairing CI.
