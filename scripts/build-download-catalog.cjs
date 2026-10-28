const fs = require('node:fs');
const path = require('node:path');
const {latestPlatforms} = require('./platform-downloads.cjs');
const kinds = {
  'server-windows': {group: 'servers', badge: 'WS', os: 'Windows · x64', primary: /-win-x64-setup\.exe$/, fallback: /-win-x64\.zip$/, label: 'Download setup.exe', alt: /-win-x64\.zip$/},
  'server-linux': {group: 'servers', badge: 'LX', os: 'Linux · x64', primary: /-linux-x64\.deb$/, fallback: /-linux-x64\.tar\.gz$/, label: 'Download DEB installer', fallbackLabel: 'Download server archive', alt: /-linux-x64\.tar\.gz$/, altLabel: 'Portable archive'},
  docker: {group: 'servers', badge: 'DK', os: 'Docker · Linux amd64', primary: /^MediaServer-Docker-.*\.zip$/, label: 'Download Compose setup'},
  'client-windows': {group: 'clients', badge: 'WC', os: 'Windows · x64', primary: /-win-x64-setup\.exe$/, fallback: /-win-x64\.msi$/, label: 'Download desktop installer', alt: /-win-x64\.msi$/},
  'server-macos': {group: 'servers', badge: 'MS', os: 'macOS · Apple Silicon / Intel', primary: /-osx-arm64\.pkg$/, fallback: /-osx-x64\.pkg$/, label: 'Apple Silicon server PKG', fallbackLabel: 'Intel server PKG', alt: /-osx-x64\.pkg$/, altLabel: 'Intel server PKG'},
  'client-linux': {group: 'clients', badge: 'LC', os: 'Ubuntu 24.04 · x64', primary: /-linux-x64\.deb$/, label: 'Download desktop DEB'},
  'client-macos': {group: 'clients', badge: 'MC', os: 'macOS · Apple Silicon / Intel', primary: /-osx-arm64\.dmg$/, fallback: /-osx-x64\.dmg$/, label: 'Apple Silicon client DMG', fallbackLabel: 'Intel client DMG', alt: /-osx-x64\.dmg$/, altLabel: 'Intel client DMG'},
  'client-android': {group: 'clients', badge: 'AN', os: 'Android · 8.0+', primary: /-debug\.apk$/, label: 'Download test APK'},
};
function escape(value) {
  return String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
function assetUrl(repository, release, asset) {
  if (!asset || typeof asset.name !== 'string' || !/^[A-Za-z0-9._-]+$/.test(asset.name)) return null;
  const prefix = `https://github.com/${repository}/releases/download/`;
  try {
    const url = new URL(asset.browser_download_url);
    if (url.origin !== 'https://github.com' || url.username || url.password || url.search || url.hash) return null;
    // Accept GitHub's literal or encoded slash in the tag, never an external asset origin.
    if (decodeURIComponent(url.href) !== prefix + release.tag_name + '/' + asset.name) return null;
    return url.href;
  } catch { return null; }
}
function catalog(repository, releases) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Invalid repository.');
  if (!Array.isArray(releases)) throw new Error('Expected a published release inventory.');
  // Reuse the release-note selector, including numeric version order and asset requirements.
  return latestPlatforms(releases).map(platform => {
    const kind = kinds[platform.id];
    const history = platform.releases.map(selected => {
      const assets = selected.assets.filter(a => assetUrl(repository, selected, a));
      const primary = assets.find(a => kind.primary.test(a.name)) || assets.find(a => kind.fallback?.test(a.name));
      if (!primary) return null;
      const alternate = assets.find(a => kind.alt?.test(a.name) && a.name !== primary.name);
      return {
        tag: selected.tag_name, version: selected.tag_name.slice(platform.id.length + 2),
        notes: `https://github.com/${repository}/releases/tag/${selected.tag_name}`,
        download: assetUrl(repository, selected, primary),
        downloadLabel: kind.primary.test(primary.name) ? kind.label : (kind.fallbackLabel || (platform.id === 'server-windows' ? 'Download portable ZIP' : 'Download MSI installer')),
        alternate: alternate ? {url: assetUrl(repository, selected, alternate), label: kind.altLabel || (platform.id === 'server-windows' ? 'Portable ZIP' : 'MSI installer')} : null,
      };
    }).filter(Boolean);
    return {...platform, ...kind, release: undefined, releases: undefined, latest: history[0], older: history.slice(1)};
  });
}
function card(platform, repository) {
  const p = platform;
  const latest = p.latest;
  const archive = p.older.length ? `<details class="history"><summary>Older versions <span>${p.older.length}</span></summary><ul>${p.older.map(r => `<li><a href="${escape(r.notes)}">v${escape(r.version)}</a><span>Release notes &amp; downloads</span></li>`).join('')}</ul></details>` : '<p class="history empty">No older published builds</p>';
  const download = latest ? `<div class="version"><span>Latest alpha</span><strong>v${escape(latest.version)}</strong></div><a class="download" href="${escape(latest.download)}">${escape(latest.downloadLabel)} <span aria-hidden="true">↗</span></a><div class="secondary-links"><a href="${escape(latest.notes)}">Release notes</a>${latest.alternate ? `<a href="${escape(latest.alternate.url)}">${escape(latest.alternate.label)}</a>` : ''}</div>` : '<p class="unavailable">No published build available yet.</p>';
  const note = p.id === 'client-android' ? 'Development-signed testing APK. Updates between builds may require reinstalling. Unsigned release APK/AAB files are for developers.' : p.group === 'servers' ? 'Includes the web interface. Configure your library after installation.' : 'Connects to an existing server. Does not install the server.';
  const guide = p.id === 'client-android' ? 'docs/clients/ANDROID.md' : p.group === 'servers' ? 'docs/GETTING-STARTED.md' : 'docs/deployment/DESKTOP-INSTALLERS.md';
  return `<article class="platform-card" data-platform="${p.id}"><div class="card-top"><span class="platform-icon" aria-hidden="true">${p.badge}</span><span class="os">${p.os}</span></div><h3>${escape(p.name)}</h3><p class="description">${escape(p.description)}</p>${download}<p class="install-note">${escape(note)} <a href="https://github.com/${repository}/blob/main/${guide}">Setup guide</a></p>${archive}</article>`;
}
function markdown(repository, platforms) {
  const lines = ['# Downloads', '', 'Choose a category, then your platform. Each platform shows its latest published alpha; older releases are collapsed beneath it.', '', '[Servers](#servers) · [Clients](#clients) · [Browser](#browser) · [Complete GitHub release history](https://github.com/' + repository + '/releases)', ''];
  for (const [group, title, intro] of [['servers', 'Servers', 'Install a server on the machine that stores your media. Each server includes the web client.'], ['clients', 'Clients', 'Connect these applications to your existing server.']]) {
    lines.push('## ' + title, '', intro, '');
    for (const p of platforms.filter(p => p.group === group)) {
      lines.push('### ' + p.name, '', p.os + ' — ' + p.description + '.', '');
      if (p.latest) {
        lines.push('**Latest: v' + p.latest.version + '**', '', '[' + p.latest.downloadLabel + '](' + p.latest.download + ') · [Release notes](' + p.latest.notes + ')' + (p.latest.alternate ? ' · [' + p.latest.alternate.label + '](' + p.latest.alternate.url + ')' : ''), '');
      } else lines.push('No published build available yet.', '');
      if (p.id === 'client-android') lines.push('The debug APK is the installable testing build. Unsigned release APK/AAB files need signing. Updates between debug builds may require reinstalling.', '');
      if (p.older.length) lines.push('<details>', '<summary>Older ' + p.name + ' versions (' + p.older.length + ')</summary>', '', ...p.older.map(r => '- [v' + r.version + '](' + r.notes + ')'), '', '</details>', '');
    }
  }
  lines.push('## Browser', '', 'The web client is included with every server build. Start your server, then open its local address. [Step-by-step setup](https://github.com/' + repository + '/blob/main/docs/GETTING-STARTED.md).', '', '## Planned platforms', '', 'Android TV and iOS do not have installable releases yet.', '', '---', '', 'This catalog is generated from published GitHub releases. It does not delete older releases, change version numbers or mark alpha builds stable.', '');
  return lines.join('\n');
}
function build({repository, releases, output, generatedAt = new Date().toISOString()}) {
  const platforms = catalog(repository, releases);
  if (!platforms.some(p => p.latest)) throw new Error('No valid published build found; refusing to replace the download catalog with an empty page.');
  const root = path.resolve(__dirname, '..');
  let html = fs.readFileSync(path.join(root, 'downloads/index.template.html'), 'utf8');
  const replacements = {
    repository: escape(repository),
    allCount: platforms.length, serverCount: platforms.filter(p => p.group === 'servers').length, clientCount: platforms.filter(p => p.group === 'clients').length,
    servers: platforms.filter(p => p.group === 'servers').map(p => card(p, repository)).join('\n'),
    clients: platforms.filter(p => p.group === 'clients').map(p => card(p, repository)).join('\n'),
    updated: escape(new Date(generatedAt).toISOString().replace('T', ' ').replace(/\.\d+Z$/, ' UTC')),
  };
  html = html.replace(/\{\{(repository|servers|clients|updated|allCount|serverCount|clientCount)\}\}/g, (_, key) => replacements[key]);
  fs.mkdirSync(output, {recursive: true});
  fs.writeFileSync(path.join(output, 'index.html'), html);
  fs.writeFileSync(path.join(output, 'README.md'), markdown(repository, platforms));
  for (const file of ['styles.css', 'app.js']) fs.copyFileSync(path.join(root, 'downloads', file), path.join(output, file));
  fs.writeFileSync(path.join(output, '.nojekyll'), '');
  return platforms;
}
module.exports = {assetUrl, catalog, markdown, build};
if (require.main === module) {
  const [input, output = 'artifacts/downloads', repository = 'Splortch/Media-server'] = process.argv.slice(2);
  if (!input) throw new Error('Usage: node scripts/build-download-catalog.cjs releases.json [output] [owner/repo]');
  const result = build({repository, releases: JSON.parse(fs.readFileSync(input, 'utf8')), output});
  console.log(`Built ${result.filter(p => p.latest).length} platform categories in ${output}`);
}
