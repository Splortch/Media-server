const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {fixtures, release} = require('./download-catalog.fixtures.cjs');
const {catalog, assetUrl, build} = require('../build-download-catalog.cjs');
const publish = require('../publish-download-catalog.cjs');
const repo = 'Splortch/Media-server';
const sha = 'a'.repeat(40);
function temp() { return fs.mkdtempSync(path.join(os.tmpdir(), 'download-catalog-')); }
function source(directory) { fs.writeFileSync(path.join(directory, 'catalog-source.json'), JSON.stringify({repository: repo, sha, generated: true})); }
function context() { return {repo: {owner: 'Splortch', repo: 'Media-server'}, eventName: 'push', ref: 'refs/heads/main', sha}; }
function mock(previous = false) {
  const writes = [];
  return {writes, github: {rest: {
    repos: {
      getBranch: async () => ({data: {commit: {sha}}}),
      getContent: async () => ({data: {content: Buffer.from(JSON.stringify({repository: repo, generated: true})).toString('base64')}}),
    },
    git: {
      getRef: async () => { if (!previous) throw Object.assign(new Error(), {status:404}); return {data: {object: {sha: 'previous'}}}; },
      getCommit: async () => ({data: {tree: {sha:'old-tree'}}}),
      createTree: async x => { writes.push(['tree', x]); return {data: {sha:'new-tree'}}; },
      createCommit: async x => { writes.push(['commit', x]); return {data: {sha:'new-commit'}}; },
      createRef: async x => { writes.push(['ref', x]); },
      updateRef: async x => { writes.push(['ref', x]); },
    },
  }}};
}
test('keeps eight fixed categories and independently sorts history without duplicate cards', () => {
  const releases = fixtures();
  releases.push({...release('server-windows', 100, ['MediaServer-v0.2.0-alpha.100-win-x64.zip']), draft:true});
  const platforms = catalog(repo, releases);
  assert.equal(platforms.length, 8);
  assert.equal(platforms[0].latest.version, '0.2.0-alpha.10');
  assert.equal(platforms[0].older[0].version, '0.2.0-alpha.9');
  assert.equal(platforms[4].latest.version, '0.1.0-alpha.17');
  assert.equal(platforms.filter(p => p.group === 'servers').length, 4);
  assert.match(platforms[0].latest.download, /setup\.exe$/);
});
test('rejects external, credentialed, mismatched, source-only and unsigned-only primary downloads', () => {
  const r = fixtures()[1]; const asset = r.assets[0];
  for (const url of ['https://evil.example/setup.exe', asset.browser_download_url + '?token=secret', asset.browser_download_url.replace('github.com', 'user:password@github.com'), asset.browser_download_url.replace('alpha.10/', 'alpha.11/')]) assert.equal(assetUrl(repo, r, {...asset, browser_download_url:url}), null);
  assert.equal(catalog(repo, [release('client-android', 18, ['release-unsigned.apk'])])[4].latest, undefined);
  assert.throws(() => catalog('bad/repo/path', []));
});
test('builds real links, collapsed history and an always-usable GitHub Markdown category index', () => {
  const dir = temp();
  try {
    const input = fixtures(); input[0].name = '<script>alert(1)</script>';
    build({repository:repo, releases:input, output:dir, generatedAt:'2026-10-28T12:00:00Z'});
    const html = fs.readFileSync(path.join(dir, 'index.html'),'utf8');
    const md = fs.readFileSync(path.join(dir, 'README.md'),'utf8');
    assert.equal((html.match(/class="platform-card"/g)||[]).length, 8);
    assert.equal((html.match(/<details class="history">/g)||[]).length, 2);
    assert.doesNotMatch(html, /<details[^>]*\bopen\b/);
    assert.doesNotMatch(html, /alert\(1\)/);
    assert.match(md, /## Servers/); assert.match(md, /## Clients/);
    assert.match(md, /<summary>Older Windows server versions/);
    assert.match(md, /github.com\/Splortch\/Media-server\/releases\/download/);
    assert.throws(() => build({repository:repo, releases:[], output:dir}));
  } finally { fs.rmSync(dir,{recursive:true}); }
});
test('untrusted and stale sources cannot publish', async () => {
  const directory = temp();
  try {
    source(directory); const m = mock();
    await assert.rejects(publish({github:m.github,context:{...context(),eventName:'pull_request'},directory}), /trusted main/);
    m.github.rest.repos.getBranch = async () => ({data:{commit:{sha:'b'.repeat(40)}}});
    assert.equal(await publish({github:m.github,context:context(),directory}), false);
    assert.equal(m.writes.length,0);
  } finally { fs.rmSync(directory,{recursive:true}); }
});
test('publication writes generated files only to downloads, with no force update or main commit', async () => {
  const directory = temp();
  try {
    build({repository:repo,releases:fixtures(),output:directory}); source(directory);
    for (const previous of [false,true]) {
      const m = mock(previous);
      assert.equal(await publish({github:m.github,context:context(),directory}), true);
      const ref = m.writes.at(-1)[1];
      assert.equal(ref.ref, previous ? 'heads/downloads' : 'refs/heads/downloads');
      if (previous) assert.equal(ref.force,false);
      assert.equal(m.writes[0][1].tree.length,6);
    }
  } finally { fs.rmSync(directory,{recursive:true}); }
});
