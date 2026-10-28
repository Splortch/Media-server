# Foundation validation

Validation performed in a Linux x64 workspace on 2026-10-26. This record distinguishes local checks from GitHub-hosted results.

| Check | Observed result |
|---|---|
| Backend solution build | Passed, zero warnings/errors |
| Backend API/security/database tests | 7 passed |
| React production build | Passed |
| Frontend component tests | 2 passed |
| ESLint | Passed |
| Prettier | Passed |
| CSharpier | Passed |
| NuGet direct/transitive vulnerability audit | Zero reported vulnerable packages |
| npm dependency audit | Zero reported vulnerabilities |
| Browser-to-real-backend integration | Passed in Chromium using generated VP9/Opus WebM |
| Self-contained Linux x64 publish | Passed, including native startup, configured listener, health and bundled UI smoke checks |
| Self-contained Windows x64 cross-publish | Passed; Windows executable not run in Linux |
| Workflow YAML, shell and bootstrap syntax | Parsed/checked locally |
| Docker image build/runtime | Passed in GitHub-hosted CI, including startup, bundled UI and private-media smoke checks; Docker unavailable locally |
| GitHub-hosted CI | Initial frontend/Linux backend/Docker jobs passed; all 7 backend tests also passed on Windows. Browser OS compatibility and Windows checkout line endings were corrected with a pinned runner and .gitattributes; see Actions for current commit status. |
| Release workflow | Not dispatched; no official release published |

The browser integration creates a fresh SQLite database, completes setup/login through the real UI, adds a media directory, scans using real FFprobe, opens the discovered video, verifies actual browser playback, saves progress, checks a 206 byte-range response and reloads to verify resume. Responsive screenshots are inspected internally using synthetic test data.

Backend tests additionally cover anonymous/private endpoint separation, setup token and antiforgery requirements, repeated setup rejection, salted Argon2id verification, allowed-root/traversal/symlink rejection, idempotent scanning with an unreadable-media case, unchanged source bytes, suffix/unsatisfiable ranges, missing sources, SQLite persistence/migrations, role denial, copied-cookie revocation on logout and authentication throttling.

Dependency audits describe advisory-feed results at validation time, not proof that the application is vulnerability-free. Public remote-access deployment, malicious local filesystem races, broad codec/device compatibility and packaged Windows execution require further validation. See SECURITY.md and the roadmap.

Repository visibility, description, Discussions, private vulnerability reporting, branch protection and organization Projects require repository-administration operations not exposed by the connected GitHub tools used for this foundation. `scripts/configure-repository.py` provides a reviewable follow-up using an authenticated GitHub CLI. Its existence does not mean those settings have been applied.


## Security, diagnostics and accessibility — 2026-10-28

PR #42 implements the tested portions of issues #1, #3 and #15. Local .NET SDK 10.0.403 builds with
warnings-as-errors and all 20 backend tests pass; all four player component tests, production web
build, lint, Prettier and CSharpier checks pass. npm audit reports zero vulnerabilities. The running
server's versioned OpenAPI was exported and TypeScript regenerated.

Hosted run 40604076305 passed the 20-test backend suite on Ubuntu and Windows. Run 40604076309 passed
Docker startup/auth/UI smoke checks and the real-backend browser flow: setup/login/library scan,
corrupt-media diagnosis, byte-range playback, progress/resume, missing-file errors, keyboard checks
and axe WCAG A/AA scans. Web and automation checks also passed on the implementation commit.

A separate local subprocess smoke test provisioned an isolated server, logged in, stopped it,
ran --reset-admin-password through stdin, verified no password echo and empty session storage,
then restarted and verified old-password rejection, new-password login and closed setup. Its data
was temporary. The CLI also rejects recovery while a live server owns the same data directory. Local Chrome launch was blocked by the workspace's Unix-socket restriction; the
browser result above is hosted CI, not a local browser or physical-device claim.

Manual assistive-technology testing, hostile-writer-safe scan handles and independent security
review remain outstanding. See [HARDENING.md](../architecture/HARDENING.md) and [ACCESSIBILITY.md](ACCESSIBILITY.md) for the exact limits. New CI runs
on subsequent commits must still pass before merging; these run IDs identify the evidence checked.

## Five server-operations issues — 2026-10-28

Local validation for the accounts (#13), reconciliation (#11), indexed browsing (#12), monitoring (#21)
and backup/update (#22) batch: 28 backend tests pass. The 100,000-item HTTP search test measured 57.1 ms
for its first bounded page in this environment (not a general production latency guarantee). Five frontend
component tests pass, including an expired-session/delayed-page regression that prevents an old account's
results from appearing after another account signs in. Fifteen Python script tests pass, including failed
migration, interrupted pointer replacement, archive traversal and copy-only rollback policies.

An actual self-contained Linux archive passed synthetic video probing, authenticated ranges, progress,
version/UI/startup, offline checkpoint, clone migration and restore gates. A separate test staged actual
0.2.0-alpha.1 and 0.2.0-alpha.2 native builds, activated both in sequence and rolled back to the first,
retaining the original data directory. These are local test builds, not published stable releases.

Restores intentionally revoke sessions; integrity/dry-run/private-mode/tampering/future-schema checks are
covered by backend tests. The release workflow now runs the real archive's staged-update checks on both
Windows and Linux. Hosted results for this batch are pending review CI; physical-device testing, GPU
adapters, codec sandboxing, signed installers and power-loss validation are not claimed.
