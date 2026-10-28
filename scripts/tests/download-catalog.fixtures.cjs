function release(id, number, files) {
  const tag = `${id}/v0.${id.startsWith('client-') ? '1' : '2'}.0-alpha.${number}`;
  return {id: number, tag_name: tag, name: 'Synthetic test fixture', draft: false, prerelease: true, published_at: '2026-10-28T12:00:00Z', assets: files.map(name => ({name, browser_download_url: `https://github.com/Splortch/Media-server/releases/download/${tag}/${name}`}))};
}
function fixtures() {
  return [
    release('server-windows', 9, ['MediaServer-v0.2.0-alpha.9-win-x64.zip']),
    release('server-windows', 10, ['MediaServer-v0.2.0-alpha.10-win-x64-setup.exe', 'MediaServer-v0.2.0-alpha.10-win-x64.zip']),
    release('server-linux', 10, ['MediaServer-v0.2.0-alpha.10-linux-x64.tar.gz']),
    release('docker', 10, ['MediaServer-Docker-v0.2.0-alpha.10.zip']),
    release('client-windows', 10, ['MediaServer-Windows-Client-v0.1.0-alpha.10-win-x64-setup.exe', 'MediaServer-Windows-Client-v0.1.0-alpha.10-win-x64.msi']),
    release('client-android', 17, ['MediaServer-Android-v0.1.0-alpha.17-debug.apk']),
    release('client-android', 7, ['MediaServer-Android-v0.1.0-alpha.7-debug.apk']),
  ];
}
module.exports = {fixtures, release};
