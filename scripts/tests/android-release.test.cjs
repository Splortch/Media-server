const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const publish = require('../publish-android.cjs');
const sha = 'a'.repeat(40);
function fixture() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'android-release-'));
  const version = '0.1.0-alpha.1';
  const names = [`MediaServer-Android-v${version}-debug.apk`, `MediaServer-Android-v${version}-release-unsigned.apk`, `MediaServer-Android-v${version}-release-unsigned.aab`, 'README.txt', 'release.json'];
  for (const name of names) fs.writeFileSync(path.join(directory, name), name === 'release.json' ? JSON.stringify({platform: 'client-android', version, commit: sha, releaseSigned: false}) : 'test fixture');
  fs.writeFileSync(path.join(directory, 'SHA256SUMS'), names.map(name => `${crypto.createHash('sha256').update(fs.readFileSync(path.join(directory, name))).digest('hex')}  ${name}\n`).join(''));
  const calls = [];
  const rest = {repos: {
    getBranch: async () => ({data: {commit: {sha}}}),
    getReleaseByTag: async () => { throw Object.assign(new Error(), {status: 404}); },
    createRelease: async data => { calls.push(['create', data]); return {data: {id: 1}}; },
    uploadReleaseAsset: async data => { calls.push(['upload', data.name]); },
    updateRelease: async data => { calls.push(['publish', data]); }
  }};
  return {directory, calls, github: {rest}, context: {repo: {owner: 'owner', repo: 'repo'}, eventName: 'push', ref: 'refs/heads/main', runNumber: 1, sha}};
}
test('publishes only after all six files upload and labels unsigned artifacts honestly', async () => {
  const f = fixture(); try { await publish(f); assert.equal(f.calls.filter(x => x[0] === 'upload').length, 6); assert.equal(f.calls.at(-1)[0], 'publish'); assert.equal(f.calls[0][1].draft, true); assert.match(f.calls[0][1].body, /sign it before installation/); } finally { fs.rmSync(f.directory, {recursive: true}); }
});
test('tampered files are rejected before creating a release', async () => {
  const f = fixture(); try { fs.appendFileSync(path.join(f.directory, 'README.txt'), 'tamper'); await assert.rejects(publish(f), /checksum/); assert.equal(f.calls.length, 0); } finally { fs.rmSync(f.directory, {recursive: true}); }
});
test('PR events cannot publish', async () => {
  const f = fixture(); try { f.context.eventName = 'pull_request'; await assert.rejects(publish(f), /main push/); assert.equal(f.calls.length, 0); } finally { fs.rmSync(f.directory, {recursive: true}); }
});
test('failed upload leaves a draft and never publishes partial artifacts', async () => {
  const f = fixture(); try { f.github.rest.repos.uploadReleaseAsset = async () => { throw new Error('upload failed'); }; await assert.rejects(publish(f), /upload failed/); assert.equal(f.calls.filter(x => x[0] === 'publish').length, 0); } finally { fs.rmSync(f.directory, {recursive: true}); }
});
