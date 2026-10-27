# Authoritative cross-device progress

SQLite is authoritative per `(userId, mediaItemId)`. Each progress row includes seconds, completed state, server update time and a monotonic revision. Completion is explicit or at least 95% of known duration. Selecting a completed title starts a new viewing from zero; an explicit current-revision update can clear completion when the user rewatches.

1. Fetch the server position and revision when opening a title.
2. Serialize local progress writes. Send `{seconds, completed, expectedRevision}` to `progress-sync`; adopt the returned revision only after acknowledgement.
3. An atomic conditional SQLite upsert accepts exactly the expected revision. A stale different update returns **409 with current server state**. A lost-response retry with the same position/completion is idempotent.
4. On conflict or uncertain connection failure, stop the local write queue and ask the user to reopen the video. Never replay a stored stale queue over another device's position. Reopening fetches fresh server state; unsent seconds can be lost, with a visible error rather than corrupted server progress.
5. Continue Watching excludes completed rows and uses server order. Clients refresh when returning to the library or refocusing/reconnecting. They do not claim instantaneous live handoff.

Example: Windows writes second 1200 at revision 8. Android must first fetch revision 8 to resume and write revision 9. A still-running stale Windows writer holding revision 7 cannot rewind it. Legacy `/api/.../progress` remains available for compatibility and increments revisions, but has last-arrival semantics; all included updated players use the guarded endpoint. Third-party legacy clients should migrate before expecting conflict protection.

Logout/revocation is enforced on subsequent API/range requests. A response already authorized by the server is not retroactively retractable. The desktop bridge cancels its local in-flight streams when disconnecting/switching sessions. Real-time cross-device playback control remains issue #16.
