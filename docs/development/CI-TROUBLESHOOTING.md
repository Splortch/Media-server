# PR CI failure investigation

Inspection: 27 October 2026. The latest main runs passed; most red checks belonged to
older Dependabot branches. A failed check is not always a failed application test.

| Affected PRs/runs | Verified cause | Resolution |
| --- | --- | --- |
| #25 Windows backend and desktop checks | `component-changes.py` opened GitHub's UTF-8 event JSON using the Windows ANSI default. An emoji in the Dependabot PR body caused `UnicodeDecodeError` before any build/test. | Explicit UTF-8 for event input, Git path output and Actions output; regression tests on Windows and Linux. |
| #36 Windows desktop | Chocolatey FFmpeg feed returned HTTP 504 and installed zero packages. The later real-backend integration test failed with `WinError 2` when launching FFmpeg. | Pin FFmpeg 7.1.1 via a commit-pinned setup action; verify both ffmpeg and ffprobe before integration tests. No tests are skipped. |
| #36 and earlier #25/#27 browser checks | Ubuntu downloads were making progress, but the five-minute FFmpeg installation step expired. | Keep apt network/lock timeouts and bounded retries, allow 15 minutes for the full dependency download, verify executables. |
| #35 Windows desktop | sha2 0.11 digest output no longer implements `LowerHex`; `format!("{:x}", digest)` fails with E0277. | Format the digest bytes and regression-test the exact credential key, preserving saved-session lookup. This repair retains sha2 0.10; the dependency upgrade must also regenerate both native and Tauri lockfiles. |
| #24, #28, #29, #30 older Windows checks | Builds and all seven tests passed, then the formatter rejected checkout line endings. | Current main already has `.gitattributes` enforcing LF. Refresh those branches before interpreting old formatting results. |
| #24/#29/#30 older browser checks | Old `ubuntu-latest` selected Ubuntu 26.04, unsupported by that Playwright browser installer. | Current main already pins Ubuntu 24.04. Refresh those branches. |

## Evidence

- [UTF-8 decoding failure](https://github.com/Splortch/Media-server/actions/runs/40442811702/job/121986903883)
- [Chocolatey 504 and missing executable](https://github.com/Splortch/Media-server/actions/runs/40440996674/job/121981045420)
- [Ubuntu package download timeout](https://github.com/Splortch/Media-server/actions/runs/40440996614/job/121981044960)
- [sha2 compile failure](https://github.com/Splortch/Media-server/actions/runs/40440983330/job/121981000944)
- [Old formatter failure after passing tests](https://github.com/Splortch/Media-server/actions/runs/40416196821/job/121899008467)
- [Old Playwright/Ubuntu incompatibility](https://github.com/Splortch/Media-server/actions/runs/40416141525/job/121898829247)

## Refreshing existing dependency PRs

Merge the reviewed CI repair first. Then update each open Dependabot branch from main (GitHub's
Update branch control, or request Dependabot to rebase). Re-running an old job alone does not
pull in repaired source/workflows; it reruns its original revision. Do not merge a dependency PR
based on an unrelated green main run. Let the updated dependency branch run all required checks.
For #35 regenerate both `clients/windows/native/Cargo.lock` and `clients/windows/src-tauri/Cargo.lock`
after the manifest bump; the application must continue building with `--locked`.

No dependency PR is merged by this investigation. Cancelled runs caused by a newer push are not
failed tests. A skipped component build is not evidence that its dependency upgrade was built.

## Separate Dependabot updater failures

These do not execute application tests and need separate follow-up:

- [glib security updater](https://github.com/Splortch/Media-server/actions/runs/40442900636):
  the dependency graph resolves glib 0.18.5, while the first fixed version is 0.20.0. Dependabot
  cannot fix this by changing just one lock entry; the upstream Tauri/GTK graph needs evaluation.
  Do not force an incompatible glib version or suppress the advisory. See client validation
  documentation for the existing platform-specific dependency limitations.
- [npm updater](https://github.com/Splortch/Media-server/actions/runs/40440890986): npm could not
  resolve the pinned typescript-eslint peer dependency within its release-age cutoff. The log
  says no eligible 8.72.1 version before 24 October, although the lock uses that recent release.
  Recheck after the cutoff advances or update the related packages together. Do not remove
  lockfiles, weaken audits or bypass release-age protection just to turn this run green.

## Local script checks

```bash
python -m unittest discover -s scripts/tests -p 'test_*.py'
node --test scripts/tests/issue-to-pr.test.cjs
```

The Python regression deliberately simulates Windows cp1252 decoding of an emoji-containing
GitHub event. Automation tests run on Windows and Ubuntu, with the stable required check name
`Agent dispatch policy` aggregating both platforms. Hosted Windows builds validate Rust and the
actual native-to-backend integration; a Linux script test is not a Windows playback test.
