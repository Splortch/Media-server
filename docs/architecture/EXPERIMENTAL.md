# Experimental settings and guest access

Sign in as an administrator and open **Experimental** in the web navigation.
Experiments are separate from personal Customize preferences. Only shipped
experiments appear here; new experiments must default to off.

## Enable guests

1. Create and scan the libraries you intend to share in Settings.
2. Open Experimental, select specific libraries, and enable Guest access.
3. Save the policy. On the sign-in page, visitors can choose Continue as guest.
4. To remove access, disable it and save. Saving any policy change revokes all
   existing guest sessions. Visitors must enter again after a change.

Anyone who can reach the server can enter without a password when enabled.
All media, including unrated/adult files, in the selected libraries is shared.
Only select libraries suitable for every visitor. New libraries are never
implicitly shared. Keep the server on a trusted network; remote access requires
trusted HTTPS. This is not a public internet streaming service.

Guests can browse, search and Direct Play authorized media. Guests cannot create
accounts, administer the server, prepare transcoding jobs, change settings, or
persist progress/history/collections. Continue Watching is empty and no account's
personal data is returned. The initial sign-in entry is web-only; native apps
retain their existing password sign-in flow.

## Security and API

Both `/api` and `/api/v1` expose:

- GET `/auth/guest`: public boolean availability, no library inventory.
- POST `/auth/guest`: CSRF-protected and rate-limited session issuance.
- GET/PUT `/experiments/guests`: administrator-only `{enabled,libraries}` policy.

A non-password Guest service identity uses existing library grants and EF query
filters. It is excluded from ordinary account management and password login.
Four-hour nonpersistent, HttpOnly sessions use the existing cookie/CSRF controls,
expire server-side, and support ordinary logout. There are at most 100 active
guest sessions; expired entries are pruned on issuance. Policy updates and session
issuance share the credential lock. An explicit endpoint allowlist denies guests
new APIs by default, including all mutations except logout. No schema migration
is needed: configuration uses existing FeaturePolicies, Users, Grants and Sessions.

Revocation denies subsequent authenticated requests. Already-open streams and
bytes already downloaded cannot be recalled; stopping the server interrupts
ongoing streams. This is not DRM. Existing accounts are unaffected.

Tests cover opt-in/CSRF, grants, private stream denial, range playback, personal
data isolation, forbidden mutation, policy revocation and expiration.
