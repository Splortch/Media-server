// Build a navigation table from published releases that actually contain installers/bundles.
const START = '<!-- platform-downloads:start -->';
const END = '<!-- platform-downloads:end -->';
const definitions = [
  ['server-windows', 'Windows server', 'Host your library on Windows x64', /-win-x64(?:-setup\.exe|\.zip)$/],
  ['server-linux', 'Linux server', 'Host your library on Linux x64', /-linux-x64\.tar\.gz$/],
  ['docker', 'Docker server', 'Run the Linux amd64 server with Compose', /^MediaServer-Docker-.*\.zip$/],
  ['client-windows', 'Windows desktop client', 'Connect a Windows PC to your server', /-win-x64(?:-setup\.exe|\.msi)$/],
  ['client-android', 'Android phone client', 'Install the debug APK on Android 8.0+', /-debug\.apk$/],
];
function latestPlatforms(releases) {
  return definitions.map(([id, name, description, asset]) => {
    const candidates = releases.filter(r => !r.draft && r.tag_name?.startsWith(id + '/v') &&
      /^v\d+\.\d+\.\d+-alpha\.[1-9][0-9]*$/.test(r.tag_name.slice(id.length + 1)) &&
      (r.assets || []).some(a => asset.test(a.name)));
    const parts = r => r.tag_name.slice(id.length + 2).split(/\.|-alpha\./).map(Number);
    candidates.sort((a, b) => {
      const av = parts(a), bv = parts(b);
      for (let i = 0; i < av.length; i++) if (av[i] !== bv[i]) return bv[i] - av[i];
      return 0;
    });
    return {id, name, description, release: candidates[0], releases: candidates};
  });
}
function downloadTable(repository, releases) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Invalid repository identity.');
  const root = `https://github.com/${repository}`;
  const rows = latestPlatforms(releases).map(p => {
    const download = p.release ? `[Open release](${root}/releases/tag/${p.release.tag_name})` : 'No published build yet';
    return `| ${p.name} | ${p.description} | ${download} |`;
  });
  rows.push(`| Web browser | Included with Windows, Linux and Docker servers | [Server setup](${root}/blob/main/docs/GETTING-STARTED.md) |`);
  return `${START}\n## All platform downloads\n\n[Browse by platform — latest builds and collapsed version history](https://github.com/${repository}/blob/downloads/README.md)\n\nEach implemented target has its own versioned alpha release. Choose a server to host media, or a client to connect to an existing server.\n\n| Platform | What to use it for | Download |\n| --- | --- | --- |\n${rows.join('\n')}\n\nAndroid TV and iOS are planned; they do not have installable releases yet. Android release APK/AAB files marked unsigned need signing; the debug APK is the installable testing build.\n${END}`;
}
function withDownloads(body, table) {
  const existing = body || '';
  const pattern = /<!-- platform-downloads:start -->[\s\S]*?<!-- platform-downloads:end -->\s*/g;
  return table + '\n\n' + existing.replace(pattern, '').trimStart();
}
module.exports = {latestPlatforms, downloadTable, withDownloads};
