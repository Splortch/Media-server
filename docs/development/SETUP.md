# Local development

Requirements: .NET SDK from `global.json`, Node.js 24, npm, Git, FFprobe/FFmpeg. Python 3 runs the audit helper. Docker is optional. No API key or paid service is required.

## Windows PowerShell

```powershell
$env:MediaServer__DataDirectory = "$env:LOCALAPPDATA\MediaServer-dev"
$env:MediaServer__AllowedRoots__0 = "D:\Movies"
dotnet restore MediaServer.sln --locked-mode
dotnet run --project backend/src/Api
```

In a second terminal run `cd frontend; npm ci; npm run dev`, then browse to `http://127.0.0.1:5173`. Get the setup token using `Get-Content "$env:LOCALAPPDATA\MediaServer-dev\setup-token"`. Protect the data directory with your user's NTFS ACL; never put it in a shared/public folder.

## Linux

```bash
export MediaServer__DataDirectory="$HOME/.local/share/media-server-dev"
export MediaServer__AllowedRoots__0="/srv/media"
dotnet run --project backend/src/Api
```

Read `setup-token` inside the chosen data directory. Unix data directory access is restricted to the current user at startup. The default data directory is relative to the backend content root; an explicit absolute path avoids surprises.

## Database migrations

```bash
dotnet tool restore
dotnet ef migrations add DescribeChange --project backend/src/Infrastructure --startup-project backend/src/Api
```

Review and commit migration, designer and snapshot files. Back up the database before upgrades. Startup applies committed migrations; missing migrations are errors, not an invitation to delete existing databases.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_URLS` | `http://127.0.0.1:5050` | Listener; stay on loopback |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | Accepted host headers |
| `MediaServer__DataDirectory` | `data` | SQLite, key ring and one-time setup token |
| `MediaServer__AllowedRoots__0` | None | Explicit absolute directory in which libraries may be added |
| `MediaServer__AllowedRoots__1` | None | Optional additional root |
| `MediaServer__FfprobePath` | `ffprobe` | Executable name or absolute binary path |
| `MediaServer__SecureCookies` | `false` | Set `true` for HTTPS deployments |
| `MEDIA_PATH` | Required for Compose | Host media directory mounted read-only at `/media` |

No allowed roots means no media scanning or streaming. Add roots via process environment before starting. Changing allowed roots can intentionally revoke access to existing library files.

## Troubleshooting

- **Empty library:** add a directory and explicitly scan. Refresh Library after scan completion. Unsupported extensions are skipped; corrupt/no-video files increment the failed count.
- **FFprobe not found:** install FFmpeg and restart your shell; configure `MediaServer__FfprobePath` if necessary.
- **Directory rejected:** use an existing absolute path beneath an allowed root. Symlinks/junctions and filesystem roots are rejected. Container paths differ from host paths.
- **Playback fails:** check file availability and browser codec/container support. The alpha does not transcode or select alternate tracks. Try H.264/AAC MP4 or VP9/Opus WebM.
- **401 after setup:** setup creates an account; sign in separately. Expired/revoked sessions require login.
- **400 CSRF error:** reload after login/logout. Writes require the token returned by `/api/auth/csrf` and its cookie.
- **429:** authentication is limited to ten requests per minute per source IP; wait a minute.
- **Interrupted scan:** restart marks pending jobs interrupted; start a new scan. Previously indexed items are retained.
- **Stale/deleted files:** scans do not delete originals or automatically remove stale database entries. Missing sources return 404.
- **Private host name rejected:** configure a precise `AllowedHosts` list; do not use wildcard hosts as a shortcut.
- **Database busy/errors:** use one server process per database and a local disk, not a network filesystem. Do not delete the DB to resolve migration failures.
- **No UI on backend port:** use Vite during development, or build the frontend and copy `frontend/dist` to backend `wwwroot` before publishing.
