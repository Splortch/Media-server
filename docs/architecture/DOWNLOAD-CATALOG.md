# Reviewed Wikimedia Commons CC0 catalog

This optional provider exposes an **operator-reviewed subset** of Wikimedia
Commons, not arbitrary search results. Only live video records with explicit CC0
1.0 dedication URLs and attribution are eligible. File names, categories, search
labels and the generic Commons copyright flag are not evidence by themselves.
CC0 waives copyright interests; Commons may still mark the underlying work as
copyrighted. Other licenses and unreviewed uploads are excluded.

## Opt in

Review the original creator/source, the dedication and any separate rights before
adding an entry. Set server-side configuration, for example:

```text
Downloads__Commons__Enabled=true
Downloads__Commons__ReviewedFiles__0__File=CC0 - Public Domain Dedication video bumper.webm
Downloads__Commons__ReviewedFiles__0__Evidence=https://commons.wikimedia.org/wiki/File:CC0_-_Public_Domain_Dedication_video_bumper.webm
```

Use the exact normalized Commons file name without `File:`. Each entry requires
an HTTPS rights-evidence link. At most 20 reviewed files can be configured.
The example record identifies Creative Commons as creator, links its original
source and records a CC0 dedication. The record's license/source evidence must
still be reviewed; this feature does not turn all Commons videos into public-domain
works or certify trademark, privacy or jurisdiction-specific rights.

Enable the administrator Download Center and explicitly approve
`upload.wikimedia.org` in its existing source-host settings. Catalog lookup has a
fixed guarded `commons.wikimedia.org` API endpoint; media delivery still uses the
normal approved-host, DNS/public-IP, redirect, TLS, size and space checks. Turning
this provider off leaves ordinary libraries and built-in URL downloads independent.

## Browse, then approve

The administrator Downloads and family-approval forms show **Reviewed CC0
catalog** only after opt-in. Search filters that reviewed selection locally.
Results show the source record, dedication URL, attribution and review evidence.
Selecting a result fills the source form but creates no transfer. Administrators
must explicitly approve the source, library match and budget through the existing
form. Family requests remain requests until that approval.

Approval tokens expire after 15 minutes. The server refetches the selected record
before approval and rejects removed rights, changed URLs/file identity, forged
tokens and insufficient budgets. The worker checks Commons' file SHA-1 identity
before staging and additionally calculates the standard SHA-256. A changed file
cannot reach library import. Source attribution/evidence is retained internally
with the queue record. Source descriptions/artwork and movie matching remain the
separate metadata feature; file titles are not claimed to be movie identification.

## Bounds, privacy and provider terms

Each result set contains at most 10 videos; file records are capped at 20,
responses at 512 KiB, requests at 30 seconds, search input at 80 characters and the
in-memory cache at one reviewed set for 10 minutes. One provider request runs at
a time with a one-second spacing; 429 responses trigger a 60–300 second backoff.
Approval always revalidates instead of relying on a cached license decision.
Unavailable/rate-limited providers produce a useful error without changing the
queue. Download byte/time/storage limits remain those of the Download Center.

Only configured public file titles or a selected public page ID are sent to
Wikimedia. Viewing history, private library filenames and local search terms are
not sent. Attribution is displayed as plain text, never provider HTML. Follow
[Wikimedia terms](https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use),
[Commons reuse guidance](https://commons.wikimedia.org/wiki/Commons:Reusing_content_outside_Wikimedia)
and the [API etiquette](https://www.mediawiki.org/wiki/API:Etiquette). Metadata
lookup uses a descriptive project User-Agent. This integration claims only the
reviewed CC0 subset; it does not advertise other catalogs.

## Validation

Automated tests cover license/evidence mapping, unsafe links, plain attribution,
caching/privacy, forged and changed approvals, and the real SQLite/worker/staging
path using controlled provider/source responses. Frontend tests verify that
selection never starts a transfer. **Live reviewed CC0 catalog validation** is an
opt-in Actions workflow running `scripts/test-commons-catalog.py` against the
actual known creator-published CC0 sample and production guarded HTTPS. It is
separate from ordinary CI so third-party outages do not break unrelated changes.
