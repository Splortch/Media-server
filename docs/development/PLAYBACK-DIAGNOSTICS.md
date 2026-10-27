# Playback and scan troubleshooting

Open **Playback diagnostics** below a video to see the chosen mode, container/codecs, browser
container support, buffered media seconds and dropped/total frames where the browser exposes them.
The buffer-fill rate is an estimate from buffer growth and the file's average bitrate, not measured
network throughput. Seeking resets the sampling window. Browser container support is not a promise
that every codec/profile in that container will decode. No transcoding fallback is added here.

Browser media errors now distinguish interruption, network, decoding and unsupported-format failures.
On failure, the web client asks the authenticated playback-info endpoint whether the server can still
open the file. Missing/inaccessible files get an actionable message and correlation reference, also
recorded without private paths in structured logs. Offline servers and expired sessions keep their
normal reconnect/login handling. Reports contain bounded categories, not URLs, cookies or probe output.

After a library scan, Settings provides a keyboard-accessible problems list with a sanitized basename,
reason and log reference. Only administrators can retrieve `/api[/v1]/scans/{id}/issues`; ordinary users
cannot see other libraries' filenames or roots. Up to 100 issues are retained per scan, while the scan's
failure count still reflects all failures. Reasons distinguish unavailable FFprobe, timeout, unreadable
media, no video track, unavailable files, denied access and unsafe paths. Raw FFprobe stderr and exception
messages never enter the API. This additive SQLite migration creates ScanIssues without changing media
or existing progress. Back up the database before upgrading.
