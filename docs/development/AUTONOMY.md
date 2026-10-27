# Issue-to-PR coding agent

This is assisted development: a human approves a focused issue, GitHub Copilot cloud agent
implements it on a branch and opens a PR, and a human reviews the result. The Media-server
application does not depend on Copilot and receives no new cloud requirement.

## Activation (repository administrator)

This feature is **not active just because these files exist**. Merge its setup PR after review,
then complete the steps below. The dispatcher deliberately fails closed until configured.

1. Enable Copilot cloud agent for this repository and the authorizing user. An eligible paid
   Copilot plan and organization policy are required. Check current pricing, usage allowance
   and GitHub Actions budget before starting tasks. No paid plan has been purchased by this setup.
2. Protect `main`: require pull requests, at least one human approval, dismissal of stale
   approvals and the existing server/web/Windows/security checks plus `Agent dispatch policy`.
   Keep agent accounts off bypass lists. Two maintainers can review each other's work; bots
   must not count as human reviewers. Configure any available policy enforcing that requirement.
   The dispatcher checks that branch protection is enabled; that boolean cannot prove every
   review/check rule, so an administrator must verify the detailed rules separately.
3. Create a fine-grained, short-lived **user** PAT scoped only to Splortch/Media-server.
   GitHub's issue-assignment API currently requires metadata read and Actions, Contents,
   Issues and Pull requests read/write. Organization approval may be necessary. Store it as
   Actions secret `COPILOT_ASSIGNMENT_TOKEN`. Do not paste it into chat, an issue, a file or
   the Copilot environment. This dispatcher credential is not forwarded to the coding agent.
   The built-in `GITHUB_TOKEN` is used only for inspection, labels and the dispatch receipt;
   it is not a substitute for the assignment user token.
4. Set Actions repository variable `ISSUE_TO_PR_ENABLED` to exactly `true`.
5. Create a small task using **Agent implementation task**. A maintainer reads the final text
   and posts exactly `/agent implement` in a new comment. Check the **Issue to PR** Actions run,
   the issue's Copilot assignee, and the resulting agent session and PR. The first real session
   must be validated after credentials and policies are configured; unit tests do not prove it.

The connected repository tools used to prepare this change cannot configure Copilot entitlement,
Actions secrets/variables, billing or branch protection. Those administrative steps remain manual.
If you prefer no assignment credential, enable the native agent and assign the issue to Copilot
in GitHub's UI instead; the dispatcher locks/authorization do not cover that manual path.

References: [GitHub assignment API and credential requirements](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api),
[Copilot repository instructions](https://docs.github.com/en/copilot/how-tos/configure-custom-instructions/add-repository-instructions).
The assignment API is in public preview; revisit these requirements when GitHub changes it.

## Authoring and running a task

Use `### Task`, `### Acceptance criteria`, `### Validation` and `### Constraints` headings
(the issue form supplies them). Include observable checkbox acceptance criteria, relevant test
commands, allowed files/behavior and dependencies. Never approve a vague multi-platform project
as one task. Good examples: one player accessibility bug, a missing API regression test, or an
isolated renderer error state. Split Android into networking, secure sessions, library and player PRs.

Only a human with current write, maintain or admin permission can dispatch. An approval comment
is a new comment, not an edit to an old one. It approves the issue title/body snapshot attached to
that event; subsequent changes require a fresh command. External reporters can propose issues,
but cannot authorize agent work. Instructions in issue text are never executed by this workflow.
Do not include secrets in task text. Maintainers must still review externally supplied tasks for
malicious instructions: repository instructions are guidance, not a security sandbox.

One accepted command makes **at most one assignment request**, with no automatic retry. The
request must target this repository's default branch. Existing human assignees are preserved.
A permanent command receipt records approver, issue-body digest and command ID. A rerun of that
same command is rejected. The dispatcher never pushes code, merges or publishes.

The `agent:in-progress` label is the durable repository-wide slot. It is created on first accepted
dispatch, and is checked across open **and closed** issues. Another open issue assigned to Copilot
also blocks dispatch. The Actions concurrency group serializes dispatcher decisions, not coding
sessions. GitHub may replace older pending workflow runs with newer ones: this is not a FIFO task
queue. If your command was cancelled before dispatch, inspect the issue and post a new command.
Direct/manual agent assignment bypasses these limits; agree to use the dispatcher consistently.

The assignment token can have wider privileges than this dispatcher uses. Keep it repository
scoped, rotate it, retain default agent isolation/firewall settings and avoid production secrets.
Only trusted default-branch code runs with that credential. No issue/PR branch is checked out,
no dependency installation or task code runs in the privileged dispatcher, and API responses
are not logged. The task agent has its own GitHub-managed runtime and permissions.

## Review, completion and failures

1. Review code, meaningful test evidence and the relevant CI runs. Verify sensitive paths and
   device-specific claims yourself. Prompt instructions do not enforce a human merge gate;
   repository rules must do that. Agent-opened workflows may need a maintainer's approval to run.
2. Request iterations on the agent PR using GitHub's supported Copilot review/comment controls.
   Keep feedback within the approved issue. CI is validation, not a substitute for review.
3. After the PR is merged or abandoned, ensure the hosted agent session has stopped. Remove
   Copilot as issue assignee and remove `agent:in-progress` manually to release the slot. Do not
   release it merely because a PR was opened. Closing an issue alone intentionally keeps the lock.
4. If assignment failed/timed out, inspect the session and issue before doing anything else:
   GitHub may have accepted the request despite a lost response. The lock is intentionally left
   in place. Cancel/finish any session, remove an obsolete Copilot assignee, then remove the label.
   Post a **new** command only if a retry is needed. Never blindly rerun the previous workflow.

Missing token, entitlement, invalid task, unprotected branch and unauthorized commands stop before
assignment. An API failure after claiming the slot leaves it locked for human inspection. Failed
setup does not silently fall back to a broader token or weaken branch checks.

## Limits and emergency stop

Set `ISSUE_TO_PR_ENABLED=false` to block new dispatches, or disable the **Issue to PR** workflow.
This does **not** stop an already running Copilot session: cancel it in GitHub, then revoke the PAT
if compromised. Review outstanding PRs and the lock before re-enabling. The dispatcher job has a
five-minute timeout and 30-second API deadlines; these do not cap the hosted coding agent's run
or bill. Configure account/organization budgets separately. No unattended schedule, automatic
retries, automatic queue processing, automatic merges, release publishing or deployment is enabled.

## Validation

Run `node --test scripts/tests/issue-to-pr.test.cjs` (Node 24, no package install). Tests use a fake
GitHub API and verify authorization, changed/edited approval, isolation, task requirements, API
eligibility, locking, repeated commands, ambiguous failures and the exact assignment payload.
`Agent automation tests` runs the same tests without secrets on PRs and pushes to main.
An actual paid agent session remains unverified until activation step 5 is performed.
