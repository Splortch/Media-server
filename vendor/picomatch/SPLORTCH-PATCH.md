# Local Picomatch security mitigation

Source: npm picomatch 4.0.7, https://github.com/micromatch/picomatch.
The original MIT LICENSE and author attribution are retained.

GHSA-fc5q-39xc-vjpf (CVE-2026-112366) affects upstream versions through
4.0.7. No fixed npm release was available on 2026-11-17. Upstream PR #194
explicitly does not fix the single-branch `+(*a)` case; this copy does not
claim that PR as a security fix.

This is a Splortch mitigation, versioned `4.0.8-splortch.1`, **not an upstream
release**. It adds one guard to lib/parse.js: repeated extglobs containing an
unescaped top-level unbounded wildcard use the parser's existing literal
fallback. This prevents the nested unbounded quantifiers responsible for
`+(*)b`, `+(*a)`, `*(*a)` and related single-branch backtracking. Escaped
stars, character classes, ordinary globs and the existing nested-extglob
handling remain available. The wildcard guard stays enabled even when the
separate recursion-depth limit is disabled.

Compatibility consequence: affected repeated-extglob patterns are matched
literally instead of expanded. This follows the upstream risky-pattern policy.
This package is used by development/build tools; it is not the application's
media search engine. Tests exercise denial-of-service examples in child
processes with hard timeouts, ordinary matching semantics, and both client
lockfiles. Client build, lint and test suites must also pass.

The root frontend and Windows dev dependencies point to this local package;
`overrides.picomatch: "$picomatch"` ensures transitive users select it too.
Docker copies the package before npm ci. Audit checks stay enabled. Local
packages are outside the registry audit database, so the explicit security
regression suite is required and runs with the automation checks.

Remove the local dependency, override and vendor directory once an upstream
release addressing this advisory is available; regenerate both npm lockfiles
and retain the denial-of-service regression tests against the replacement.

Upstream npm tarball SHA-512:

```text
a9c26ef3c436216a89b030f9dbd24a31dc069bf76f2275b81ef427470887f49b62849bf318eb1c0ed1c4df1d6904a794393cac43c91598b9c9da426eef285c18
```

SPLORTCH-PATCH.diff records the parser changes. Upstream development-only
scripts and dev dependencies are omitted from the vendored manifest.
