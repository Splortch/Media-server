# Accounts, libraries, search and monitoring

## Authorization

Users can be Admin or Viewer. Only administrators can create/disable accounts, assign roles/library grants,
reset passwords, classify media, inspect all sessions, or read server metrics. New accounts start with **no
library access**. Recovery administrator #1 cannot be disabled/demoted. Changing credentials or permissions
revokes the affected user's sessions; already delivered video bytes cannot be recalled.

MediaDb applies per-request EF query filters to libraries and items, including raw search roots. Every item
lookup, metadata/track route, progress/report operation, Continue Watching join and stream lookup uses these
filters. A denied item returns 404. System/background contexts are explicit trusted contexts with no HTTP
principal; anonymous HTTP contexts use user 0 and cannot obtain system visibility. IgnoreQueryFilters is used
only by trusted scanner operations and the admin-only missing-entry view. Tests cover both legacy/v1 routes.

Maximum ratings are operator-assigned categories: General 10, Teen 13, Mature 16 and Adult 18. Zero means
unknown. Rating-restricted viewers cannot see unknown items. These categories are not automatic regional
film classifications; classify items manually in the details screen. Admins bypass library/rating restrictions.
Legacy users retain previous library visibility during migration; new accounts require explicit access.
New account names use ASCII letters/digits/underscore/dash/dot, with case-insensitive uniqueness.

Watch history has its own per-user/media record (last played, last position, last completion), independent
of mutable resume positions. Accepted progress and history writes share a transaction; a failed history
write cannot leave a partial resume update. The history API returns the last 200 visible titles. It is not a complete event
log of every rewatch. Session/device lists expose non-bearer handles; no password hash or raw cookie is returned.
The web client clears private state on expiration/logout and ignores delayed pagination responses from the
previous account.

## Library reconciliation

Libraries have movie/TV types and per-library watch/hash settings. Types do not imply online metadata or
season grouping (#10). Monitoring is opt-in. File events debounce for two seconds; a ten-minute full reconciliation
covers missed events. Watcher errors trigger a rebuild/rescan. Queued scans share a bounded scheduler, and
inaccessible/failed traversals never mark the entire library missing.

A complete traversal marks absent metadata entries as missing; originals and history are never deleted.
Optional full-file SHA-256 hashing detects duplicates and preserves the media ID for an unambiguous same-library
move. If multiple vanished files have the same hash, the scanner does not guess which history to transfer.
Cross-library moves and ambiguous moves are new IDs. Hashing consumes disk bandwidth and is opt-in.
Missing entries and duplicate suggestions are admin-only and provide no destructive cleanup action.
Media roots remain trusted/read-only as described in HARDENING.md; codecs are not OS-sandboxed.

## Indexed browsing

The web client loads pages of 60 rather than downloading the whole library. `/media-page` accepts an invariant
case-folded title prefix, optional library/codec/owned-collection filters, and a bounded opaque cursor. Results
use a SearchTitle/Id index and keyset pagination, with at most 200 rows per page. SQL inputs are bound values;
collections are typed criteria, not executable expressions. Cursors validate shape and filter identity; they
are not authorization tokens. Query filters apply on every page, even after permissions change.

The title-prefix UI is intentionally labelled as such. The legacy contains-search endpoint remains compatible
for existing native clients. Smart collections save per-user prefix/library/codec rules (up to 50). A one-time
.NET normalization fixes SQLite's ASCII-only lower() behavior on older Unicode filenames; scanning maintains
that index thereafter. Restores rebuild the disposable index marker.

A synthetic 100,000-item API test checks bounded pages, distinct cursors and isolation, with a 10-second cold
CI safety budget. This is a functional regression budget, not a production latency promise. Measure on your
actual storage and server before setting a tighter service target.

## Monitoring

Admin Settings displays server-process CPU (normalized across available logical processors), process/managed
memory, data-volume free/total capacity, uptime, queued/running scans and active authenticated HTTP transfer
requests with bytes read. Finished transfers disappear even if a buffered video keeps playing. CPU is null on
its first sample; unavailable disk values remain null. Paths and bearer session identifiers are never returned.
GPU adapters, transcoding rates and transcoding queues are explicitly unavailable until their respective
engines exist. The web client polls every five seconds; no external telemetry service is involved.
