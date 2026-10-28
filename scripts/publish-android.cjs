const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

module.exports = async ({github, context, directory = 'artifacts/android'}) => {
  if (context.eventName !== 'push' || context.ref !== 'refs/heads/main') throw new Error('Android publication requires a main push.');
  const number = String(context.runNumber);
  if (!/^[1-9][0-9]{0,4}$/.test(number) || Number(number) > 65535 || !/^[a-f0-9]{40}$/.test(context.sha)) throw new Error('Invalid release identity.');
  const version = `0.1.0-alpha.${number}`, tag = `client-android/v${version}`;
  const names = [`MediaServer-Android-v${version}-debug.apk`, `MediaServer-Android-v${version}-release-unsigned.apk`, `MediaServer-Android-v${version}-release-unsigned.aab`, 'release.json', 'README.txt'];
  const checks = fs.readFileSync(path.join(directory, 'SHA256SUMS'), 'utf8').trim().split('\n');
  if (checks.length !== names.length) throw new Error('Invalid checksum inventory.');
  const seen = new Set();
  for (const line of checks) {
    const match = /^([a-f0-9]{64})  ([A-Za-z0-9.-]+)$/.exec(line);
    if (!match || !names.includes(match[2]) || seen.has(match[2])) throw new Error('Unexpected release file.');
    seen.add(match[2]);
    const hash = crypto.createHash('sha256');
    for await (const chunk of fs.createReadStream(path.join(directory, match[2]))) hash.update(chunk);
    if (hash.digest('hex') !== match[1]) throw new Error('Android artifact checksum mismatch.');
  }
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'release.json'), 'utf8'));
  if (manifest.platform !== 'client-android' || manifest.version !== version || manifest.commit !== context.sha || manifest.releaseSigned !== false) throw new Error('Android source identity mismatch.');
  const repo = context.repo;
  async function head() {
    const {data} = await github.rest.repos.getBranch({...repo, branch: 'main'});
    if (data.commit.sha !== context.sha) throw new Error('Main advanced; skip stale Android publication.');
  }
  await head();
  let existing;
  try { existing = (await github.rest.repos.getReleaseByTag({...repo, tag})).data; }
  catch (e) { if (e.status !== 404) throw e; }
  if (existing && !existing.draft) return; // Published artifacts are immutable.
  if (existing && existing.target_commitish !== context.sha) throw new Error('Draft belongs to a different source.');
  const body = `## Android phone client alpha\n\nA native Kotlin/Compose application using Media3. This client connects to your existing server; it does not install a server. Requires Android 8.0+ and server API v1 with prefix search.\n\n### Which download?\n- **debug.apk:** installable test application. Uses an ephemeral debug signing key; uninstall an older build if an upgrade is rejected.\n- **release-unsigned.apk:** unsigned developer artifact; sign it before installation.\n- **release-unsigned.aab:** unsigned Android App Bundle for signing/distribution tooling; not directly installable.\n- **SHA256SUMS:** verify file integrity. No production signing credentials are included.\n\n### Getting started\nStart and configure your media server in its web interface, then connect/sign in in the app. Browse, search by title prefix, open details and play compatible Direct Play media. Progress resumes across devices using the server. Remote connections require trusted HTTPS; private USB testing is documented.\n\n[Step-by-step Android setup](https://github.com/${repo.owner}/${repo.repo}/blob/${context.sha}/docs/clients/ANDROID.md)\n\n### Validation and limits\nAPK assembly, lint/JVM tests and an API 35 emulator test against the real backend passed before publication. The test renders synthetic VP9/Opus media and checks resume, progress conflict handling and session revocation. This is not physical-phone or universal codec certification. No offline downloads, PiP, Cast, background audio service, subtitle/audio switching or transcoding fallback yet.\n\nSource: ${context.sha}\nIndependent Android version: ${version}`;
  const release = existing || (await github.rest.repos.createRelease({...repo, tag_name: tag, target_commitish: context.sha, name: `Media Server Android Client v${version}`, draft: true, prerelease: true, body})).data;
  if (existing) for (const asset of existing.assets) await github.rest.repos.deleteReleaseAsset({...repo, asset_id: asset.id});
  for (const name of [...names, 'SHA256SUMS']) {
    await github.rest.repos.uploadReleaseAsset({...repo, release_id: release.id, name, data: fs.readFileSync(path.join(directory, name))});
  }
  await head();
  await github.rest.repos.updateRelease({...repo, release_id: release.id, body, draft: false, prerelease: true, make_latest: 'false'});
};
