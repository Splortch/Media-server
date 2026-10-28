
const {targets} = require('./release-targets.cjs');
const {downloadTable, withDownloads} = require('./platform-downloads.cjs');
function platformNotes(target, sha, repository, image, {serverInstaller = false, unixInstallers = false} = {}) {
  const docs = 'https://github.com/' + repository + '/blob/' + sha + '/docs/deployment/RELEASES.md';
  const common = '\n\n### Before you install\n\nThis is an early alpha, intended for testing. Builds are unsigned. Back up existing server data before upgrading. HTTPS is required for safe remote access; server downloads bind to localhost by default.\n\n### Verify your download\n\nDownload SHA256SUMS to check file integrity. release.json records the platform, version and source commit. GitHub’s automatically added source archives are project source code, not installers.\n\n[Full setup and troubleshooting](' + docs + ') · [Source commit](https://github.com/' + repository + '/commit/' + sha + ')';
  const text = {
    'server-windows': [
      'Host your media library on a **Windows x64 PC**. This is the server: it scans your media and streams it to the included browser interface or a separate client. You do not need the Windows desktop client to use it.',
      '### Download\n\n' + (serverInstaller ? 'Choose **MediaServer-v' + target.version + '-win-x64-setup.exe** for per-user installation with a tray controller. The **.zip** remains the portable alternative. ' : 'Choose **MediaServer-v' + target.version + '-win-x64.zip**. ') + 'The server includes its .NET runtime and web interface; no .NET SDK installation is needed.',
      (serverInstaller ? '### Get started\n\n1. Run the server setup.exe (no administrator account required).\n2. Start Media Server from the finish page or Start menu. Choose a media folder and optionally install verified FFprobe tools.\n3. Copy the token from the setup window into the browser and create your administrator.\n4. Sign in, add the selected media folder in Settings, and scan it.\n5. The tray menu opens the web UI, controls sign-in startup, and stops the server. Exit it before upgrades or uninstall.\n\nPortable alternative:\n\n' : '') + '### Get started\n\n1. Extract the ZIP into a folder.\n2. Double-click **Start-MediaServer.cmd** and choose your media folder.\n3. If FFprobe is missing, accept the optional verified download or install it yourself.\n4. Follow the browser setup and use the setup token displayed by the launcher. Add your library in Settings, then scan it.\n5. Keep the server console open while streaming.',
      '### Data and updates\n\nData is stored separately in %LOCALAPPDATA%\\Splortch\\MediaServer. Stop the old server before starting a newer download. Scanning does not modify or delete original media files.'
    ],
    'server-linux': [
      'Host your media library on a **Linux x64 machine**. This server includes the browser interface and can serve clients on other operating systems once secure network access is configured.',
      '### Download\n\n' + (unixInstallers ? 'Choose **MediaServer-v' + target.version + '-linux-x64.deb** for installation on Ubuntu 24.04 x64 (`sudo apt install ./MediaServer-v' + target.version + '-linux-x64.deb`). Launch Media Server from your application menu. The **.tar.gz** remains portable. ' : '') + 'Choose **MediaServer-v' + target.version + '-linux-x64.tar.gz** for portable use. The .NET runtime and web interface are included. This build targets glibc-based Linux; Alpine/musl and ARM64 are not supported by this archive.',
      '### Get started\n\n1. Extract the archive and open a terminal in its folder.\n2. Install FFprobe; on Ubuntu, run `sudo apt install ffmpeg`.\n3. Run `./start-media-server.sh` and choose an existing absolute media-folder path.\n4. Open the local URL shown by the launcher and read the generated setup-token file.\n5. Complete administrator setup, add your library in Settings, and scan it.',
      '### Data and updates\n\nData defaults to ${XDG_DATA_HOME:-$HOME/.local/share}/media-server. Stop the server before replacing the application folder. Keep your existing data directory and back it up before an upgrade.'
    ],
    'client-windows': [
      'Watch media from an existing Media Server in a **dedicated Windows x64 desktop application**. The server can run on Windows, Linux or Docker; it does not need to run on the same computer.',
      '### Download\n\nChoose **MediaServer-Windows-Client-v' + target.version + '-win-x64-setup.exe** for the normal installer, or the **.msi** alternative. Install only one. This is a real Tauri desktop application; it does not install or start a media server.',
      '### Get started\n\n1. Install and launch the application.\n2. Enter your server address. Use HTTPS for connections beyond localhost.\n3. Log in with your server account.\n4. Browse or search your library, play a compatible video, and resume saved playback progress.',
      '### Playback requirements\n\nPlayback uses Windows WebView2 and depends on codecs supported by the installed runtime and operating system. These installers do not promise universal codec support or native libmpv playback. The application needs access to your running server; offline downloads are not implemented.'
    ],
    'server-macos': [
      'Host your media on **macOS**. Choose **osx-arm64** for Apple Silicon (M-series) or **osx-x64** for an Intel Mac. The self-contained server includes the web interface.',
      '### Get started\n\n1. Install the matching **.pkg** (administrator authorization required for /Applications).\n2. Install FFmpeg/FFprobe with `brew install ffmpeg`.\n3. Open **/Applications/MediaServer/Start-MediaServer.command**. Choose your absolute media directory.\n4. Open http://127.0.0.1:5050 and read the setup-token file shown in Terminal. Create your administrator, add the library, and scan.\n5. Keep Terminal open; Control-C stops the server. A **.tar.gz** portable alternative is also provided.',
      '### Data and security\n\nData stays in **~/Library/Application Support/Splortch/MediaServer**. Stop before replacing the application; uninstall by removing /Applications/MediaServer, preserving your data. Packages are unsigned and not notarized; Gatekeeper may block installation. Production signing requires Apple credentials; do not disable Gatekeeper or certificate verification. Folder privacy permissions may require approval. Minimum supported macOS 14 for the .NET 10 server; physical Mac installation has not been verified.'
    ],
    'client-linux': [
      'A dedicated **Linux x64 desktop client**, connecting to your existing server on any supported OS. This package does not install a server. Ubuntu 24.04 is the build/test baseline; older glibc distributions are not promised.',
      '### Get started\n\nInstall **MediaServer-Desktop-Client-v' + target.version + '-linux-x64.deb** with `sudo apt install ./MediaServer-Desktop-Client-v' + target.version + '-linux-x64.deb`. Launch Media Server Client, enter the server origin (HTTPS beyond localhost), and sign in.',
      '### Playback and sessions\n\nPlayback uses WebKitGTK/GStreamer, not WebView2. Codec availability depends on the installed media plugins. Sessions stay in memory and end on application exit; secure persistent Linux credentials are not implemented. Custom controls and server-authoritative playback progress are shared with Windows. No universal codec support or physical desktop testing is claimed.'
    ],
    'client-macos': [
      'A dedicated **macOS desktop client** for an existing Media Server. Choose **osx-arm64** for Apple Silicon or **osx-x64** for Intel; minimum macOS 14. The server can run on another operating system.',
      '### Get started\n\nOpen the matching **.dmg**, drag **Media Server Client** to Applications, then launch it. Enter your server origin using HTTPS beyond localhost and sign in. This client does not install the server.',
      '### Playback, sessions and signing\n\nPlayback uses WKWebView and macOS codecs; Direct Play compatibility differs from Windows. Sessions stay in memory only. DMGs are unsigned/not notarized and may be blocked by Gatekeeper. Apple signing/notarization credentials are required for frictionless public installation. Do not globally disable Gatekeeper. Physical Mac playback and signing are not verified.'
    ],
    'docker': [
      'Run the **Linux amd64 server as a Docker container**, with its web interface included. Choose this if you already use Docker Compose and want a prebuilt image rather than a local source build.',
      '### Download\n\nChoose **MediaServer-Docker-v' + target.version + '.zip** for the Compose setup. The container image is **' + image + '**. Windows Docker Desktop must use Linux containers.',
      '### Get started\n\n1. Install Docker with Compose and extract the setup ZIP.\n2. Copy .env.example to .env and set MEDIA_PATH to your host media folder.\n3. Run `docker compose up -d`.\n4. Run `docker compose exec media-server cat /data/setup-token`.\n5. Open http://127.0.0.1:5050, complete setup, and use **/media** when adding the library.',
      '### Storage and updates\n\nThe media mount is read-only. Database and settings live in the server-data volume; preserve and back it up when upgrading. The Compose file pins this version. A private GHCR package requires `docker login ghcr.io`.',
      '### Other downloads\n\nCONTAINER-PACKAGES.txt lists installed packages, FFMPEG-LICENSE.txt records FFmpeg license details, and Container-Corresponding-Source.tar.gz provides corresponding dependency source for license compliance. These files are not required to start the container. The aggregate image includes dependencies under their own licenses.'
    ]
  };
  if (!text[target.id] || !/^[a-f0-9]{40}$/.test(sha) || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Invalid release description identity.');
  return '# ' + target.name + ' ' + target.version + '\n\n' + text[target.id].join('\n\n') + common;
}
function combinedNotes(server, client, sha, repository) {
  return '# Combined alpha download archive\n\nThis earlier release contains several platforms together. Choose the download that matches what you want to run. Future releases list each platform separately.\n\n' +
    targets(server, client).map(t=>platformNotes(t,sha,repository,'ghcr.io/'+repository.toLowerCase()+':v'+server)).join('\n\n---\n\n');
}
async function syncReleaseNotes({github, context}) {
  const releases = await github.paginate(github.rest.repos.listReleases,{...context.repo,per_page:100});
  const repository = context.repo.owner + '/' + context.repo.repo;
  const table = downloadTable(repository, releases);
  for (const release of releases) {
    if (release.draft || !release.prerelease || !/^[a-f0-9]{40}$/.test(release.target_commitish)) continue;
    if (/^client-android\/v0\.1\.0-alpha\.[1-9][0-9]{0,4}$/.test(release.tag_name)) {
      const body = withDownloads(release.body, table);
      if (release.body !== body) await github.rest.repos.updateRelease({...context.repo,release_id:release.id,body});
      continue;
    }
    const match = /^(?:(server-windows|server-linux|server-macos|client-windows|client-linux|client-macos|docker)\/)?v0\.([12])\.0-alpha\.([1-9][0-9]{0,4})$/.exec(release.tag_name);
    if (!match) continue;
    const [,id,minor,number] = match;
    if ((id?.startsWith('client-') ? minor !== '1' : minor !== '2')) continue;
    const server = '0.2.0-alpha.'+number, client='0.1.0-alpha.'+number;
    const target = targets(server,client).find(t=>t.id===id);
    const description = id ? platformNotes(target,release.target_commitish,repository,'ghcr.io/'+repository.toLowerCase()+':v'+server,{serverInstaller:(release.assets||[]).some(a=>a.name===`MediaServer-v${server}-win-x64-setup.exe`),unixInstallers:(release.assets||[]).some(a=>a.name.endsWith('-linux-x64.deb'))}) : combinedNotes(server,client,release.target_commitish,repository);
    const body = withDownloads(description, table);
    if (release.body !== body) await github.rest.repos.updateRelease({...context.repo,release_id:release.id,body});
  }
}
module.exports = {platformNotes,combinedNotes,syncReleaseNotes};
