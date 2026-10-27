# GLib VariantStrIter soundness backport

## Problem and selected fix

RUSTSEC-2024-0429 / GHSA-wrw7-89jp-8q8g affects the registry glib 0.18.5 crate.
`VariantStrIter::impl_get` passes an immutable pointer reference to a variadic C
function which writes through it. Optimized builds can discard that write and
subsequently dereference a null pointer. Debug-only tests are insufficient.

Tauri's GTK3/WebKitGTK dependencies require the glib 0.18 API. No glib 0.18.6 is
published on crates.io, and glib 0.20 is not API compatible with that dependency
graph. Merely changing a lockfile version is not a fix.

The application pins gtk-rs/gtk-rs-core commit
`ea720152f28e293ef4362ee844ee5cc499f32d2a` through Cargo's registry patch mechanism.
This is the **unmerged, closed 0.18 backport PR**, not a new official release.
Its only source change from its parent is the same two-expression correction
already merged upstream for the newer API: `let mut p` and `&mut p`.

References:
- [RustSec advisory](https://rustsec.org/advisories/RUSTSEC-2024-0429.html)
- [Original merged fix, PR 1343](https://github.com/gtk-rs/gtk-rs-core/pull/1343)
- [API-compatible backport, PR 2009](https://github.com/gtk-rs/gtk-rs-core/pull/2009)
- [Declined patch-release request, issue 2010](https://github.com/gtk-rs/gtk-rs-core/issues/2010)

The immutable revision is essential. Do not replace it with a moving branch or
an arbitrary fork. glib-sys, gobject-sys, gio-sys and glib-macros are patched to
the same revision so GTK consumers share one coherent set of FFI types. The
Cargo lockfile records the exact git source instead of the affected registry
crate. The crate still truthfully identifies its 0.18 API; no version is
fabricated to evade an advisory.

## Verification and limitations

`scripts/check-glib-patch.py` verifies both the application and regression
lockfiles resolve all five packages to the expected revision. It reads the
resolved glib source and requires the actual mutable-pointer correction.
The `GLib optimized regression` CI job then runs `cargo test --release --locked`
against that source, covering next, next_back, nth, nth_back, last, empty strings,
Unicode and empty arrays. Existing Windows compilation, unit tests, real-backend
integration, installers, startup smoke and dependency audits remain enabled.

No audit ignore entry, advisory dismissal, fake crate version or certificate
bypass is introduced. Version-based scanners may not understand a patched git
source. An absence of an advisory after changing source is not proof of safety;
the source guard and optimized executable regression are the primary checks.
GitHub must re-evaluate its dependency graph after this change reaches main;
alert closure cannot be verified from an unmerged PR or a local audit alone.

GLib/GTK is not in the Windows target dependency graph, and this change does not
add a Linux desktop product. The Linux test deliberately builds and executes the
otherwise platform-specific vulnerable path, using GLib development libraries.
This is a narrow dependency security backport; authentication and playback are
unchanged.

## Maintenance

Keep both patch sections (application and regression) identical. A source or
lockfile change must pass the source guard, optimized regression and full Windows
checks. Git dependencies are pinned deliberately and need manual review for
replacement. The unrelated proc-macro-error maintenance warning is not addressed
or suppressed by this change.

Remove the overrides only when a reviewed supported Tauri dependency graph can
resolve an officially fixed GLib release, or when replacing this backport with
an equally reviewed compatible security fix. Regenerate both lockfiles, update
the guard and regression, run optimized tests, and verify the GitHub alert state.
Do not force glib 0.20 into GTK3's 0.18 API requirements.
