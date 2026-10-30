# Family movie requests

Implements #54 using the optional-feature controls from #51. This is a local
request/review system, not a downloader or acquisition integration.

## Use it

1. An administrator opens Settings → Customize → Server-wide availability and
   enables **Movie requests**. The module starts disabled on both new and upgraded
   installations. Users may separately hide its navigation in Customize.
2. Open **Movie requests**, or **Request a movie** from an empty search result.
3. Search for a title first: accessible library titles and visible existing
   requests appear. Support another member's open request instead of duplicating
   it, or enter a title, optional year and note, then choose a target library.
4. Administrators open Review request and choose Requested, Considering, Added or
   Declined. Added requires an explicit library title selection. A fuzzy match
   never changes status. Replies are visible locally; there is no external inbox.
5. Authors can withdraw requests, and administrators can remove inappropriate
   requests, using an explicit confirmation. Removal also removes its support.
6. Disabling the module hides its actions and rejects request endpoints, while
   retaining records. Re-enabling restores them. Library deletion deliberately
   cascades its request records; no original media files are changed.

Request title/note and replies are visible to administrators and members allowed
the target library. Rating-restricted accounts see their own requests only,
preventing disclosure of unclassified request titles from other users. An Added
link is returned only while the viewer can access that media item. Guests cannot
use this module. Losing a library grant hides its requests and linked titles.

## API v1 (also available under `/api`)

- GET `/api/v1/requests?q=...&before=...`: up to 200 visible records ordered by
  descending ID. Use the final ID as `before` for the next page.
- GET `/requests/{id}`: visible details, owner flag, aggregate support and revision.
- POST `/requests`: `{libraryId,title,year?,note?}`. Visible normalized duplicates
  return 409. Accessible exact library-title matches return 409; neither response
  exposes another user's inaccessible requests or media.
- PUT/DELETE `/requests/{id}/support`: idempotent per-member support, no voter names.
- DELETE `/requests/{id}`: author or administrator only; permanent removal.
- PUT `/requests/{id}`: admin review `{state,reply?,mediaItemId?,revision}`. Only
  Added accepts/requires a title link, from the request's library. Stale review
  revisions return 409 and require refresh, preventing silent overwrites.

The full contract and TypeScript request/view types are generated from OpenAPI.
Every request endpoint requires authentication, the enabled module and existing
CSRF protections on writes. Submission and support have a per-user rate limit.
Titles are limited to 160 characters, notes/replies to 500, years to 1888–2100,
requests to 50 per user and 5,000 per server. Results are paginated and projected
in one database query. Duplicate checking/insertion is serialized across API
aliases inside the exclusive single-server process. No filesystem path, key,
password, session ID or supporter identity appears in responses.

The additive EF migration creates MovieRequests and RequestSupport; backup the
server database before upgrading. This first UI is web-only. Native applications
can use the same versioned endpoints; no native request UI is claimed.
