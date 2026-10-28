// Stable release identities shared by the CI gate and publisher.
function targets(server, client) {
  if (!/^0\.2\.0-alpha\.[1-9][0-9]{0,4}$/.test(server) || !/^0\.1\.0-alpha\.[1-9][0-9]{0,4}$/.test(client)) throw new Error('Invalid platform versions.');
  return [
    {id:'server-windows', name:'Media Server for Windows', version:server, files:[`MediaServer-v${server}-win-x64.zip`,`MediaServer-v${server}-win-x64-setup.exe`], instructions:'Run the server setup.exe, or extract the portable ZIP and run Start-MediaServer.cmd. The web UI is included.'},
    {id:'server-linux', name:'Media Server for Linux', version:server, files:[`MediaServer-v${server}-linux-x64.tar.gz`,`MediaServer-v${server}-linux-x64.deb`], instructions:'Extract the archive, install FFprobe, and run ./start-media-server.sh. The web UI is included.'},
    {id:'client-windows', name:'Media Server Windows Client', version:client, files:[`MediaServer-Windows-Client-v${client}-win-x64-setup.exe`,`MediaServer-Windows-Client-v${client}-win-x64.msi`], instructions:'Install either the EXE or MSI, then connect to a running media server. This client does not install the server.'},
    {id:'docker', name:'Media Server Docker', version:server, files:[`MediaServer-Docker-v${server}.zip`,'CONTAINER-PACKAGES.txt','FFMPEG-LICENSE.txt','Container-Corresponding-Source.tar.gz'], instructions:'Extract the setup ZIP, copy .env.example to .env, set MEDIA_PATH, then run docker compose up -d. The source archive is provided for license compliance; installation does not require extracting it.'}
    ,{id:'server-macos', name:'Media Server for macOS', version:server, files:['osx-arm64','osx-x64'].flatMap(rid=>[`MediaServer-v${server}-${rid}.pkg`,`MediaServer-v${server}-${rid}.tar.gz`]), instructions:'Install the PKG for Apple Silicon or Intel, then open /Applications/MediaServer/Start-MediaServer.command. Install FFprobe separately; the web UI is included.'}
    ,{id:'client-linux', name:'Media Server Linux Desktop Client', version:client, files:[`MediaServer-Desktop-Client-v${client}-linux-x64.deb`], instructions:'Install the DEB with apt, then open Media Server Client and connect to your server. Ubuntu 24.04 x64 baseline.'}
    ,{id:'client-macos', name:'Media Server macOS Desktop Client', version:client, files:['osx-arm64','osx-x64'].map(rid=>`MediaServer-Desktop-Client-v${client}-${rid}.dmg`), instructions:'Open the DMG for Apple Silicon or Intel and drag Media Server Client into Applications. Connect to your existing server.'}
  ].map(target => ({...target, tag:`${target.id}/v${target.version}`}));
}
module.exports = {targets};
