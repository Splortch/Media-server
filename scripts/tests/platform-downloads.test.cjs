const {test} = require('node:test');
const assert = require('node:assert/strict');
const {latestPlatforms, downloadTable, withDownloads} = require('../platform-downloads.cjs');
const {syncReleaseNotes} = require('../release-notes.cjs');
const sha = 'a'.repeat(40);
function release(id, n, file) {
  return {id: n, tag_name: `${id}/v0.${id.startsWith('client-') ? '1' : '2'}.0-alpha.${n}`, target_commitish: sha, draft: false, prerelease: true, body: 'Existing Android instructions', assets: [{name: file}]};
}
const fixtures = () => [
  release('server-windows', 9, 'MediaServer-v0.2.0-alpha.9-win-x64.zip'),
  release('server-windows', 10, 'MediaServer-v0.2.0-alpha.10-win-x64.zip'),
  release('server-linux', 10, 'MediaServer-v0.2.0-alpha.10-linux-x64.tar.gz'),
  release('docker', 10, 'MediaServer-Docker-v0.2.0-alpha.10.zip'),
  release('client-windows', 10, 'MediaServer-Windows-Client-v0.1.0-alpha.10-win-x64.msi'),
  release('server-macos', 10, 'MediaServer-v0.2.0-alpha.10-osx-arm64.pkg'),
  release('client-linux', 10, 'MediaServer-Desktop-Client-v0.1.0-alpha.10-linux-x64.deb'),
  release('client-macos', 10, 'MediaServer-Desktop-Client-v0.1.0-alpha.10-osx-arm64.dmg'),
  release('client-android', 17, 'MediaServer-Android-v0.1.0-alpha.17-debug.apk'),
].map((r, i) => ({...r, id: i + 1}));
test('selects numeric latest versions independently; ignores drafts and empty releases', () => {
  const releases = fixtures();
  releases.push({...release('client-android', 18, 'debug.apk'), draft: true});
  releases.push({...release('client-android', 19, 'source.zip'), assets: []});
  const platforms = latestPlatforms(releases);
  assert.equal(platforms[0].release.tag_name, 'server-windows/v0.2.0-alpha.10');
  assert.equal(platforms[4].release.tag_name, 'client-android/v0.1.0-alpha.17');
});
test('shows all five real releases and describes web/future platforms accurately', () => {
  const table = downloadTable('Splortch/Media-server', fixtures());
  for (const p of latestPlatforms(fixtures())) assert.ok(table.includes(`/releases/tag/${p.release.tag_name}`));
  assert.match(table, /Included with Windows, Linux and Docker/);
  assert.match(table, /Android TV and iOS are planned/);
  assert.match(table, /unsigned need signing/);
  assert.throws(() => downloadTable('malicious/repo/path', []));
  assert.match(downloadTable('owner/repo', []), /No published build yet/);
});
test('updates the navigation block idempotently without removing platform instructions', () => {
  const first = withDownloads('My existing instructions', downloadTable('owner/repo', fixtures()));
  assert.equal(withDownloads(first, downloadTable('owner/repo', fixtures())), first);
  assert.equal((first.match(/platform-downloads:start/g) || []).length, 1);
  assert.ok(first.endsWith('My existing instructions'));
});
test('sync includes Android and preserves assets and release flags; repeats cause no writes', async () => {
  const releases = fixtures(); const writes = [];
  const github = {
    paginate: async () => releases,
    rest: {repos: {
      listReleases() {},
      updateRelease: async data => { writes.push(data); releases.find(r => r.id === data.release_id).body = data.body; },
    }},
  };
  const context = {repo: {owner: 'owner', repo: 'repo'}};
  await syncReleaseNotes({github, context});
  assert.equal(writes.length, releases.length);
  assert.ok(writes.find(w => w.release_id === releases.find(r => r.tag_name.startsWith('client-android/')).id).body.endsWith('Existing Android instructions'));
  for (const w of writes) assert.deepEqual(Object.keys(w).sort(), ['body', 'owner', 'release_id', 'repo']);
  const count = writes.length;
  await syncReleaseNotes({github, context});
  assert.equal(writes.length, count);
});
