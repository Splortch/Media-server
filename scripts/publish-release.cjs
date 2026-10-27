const fs = require('node:fs');
const crypto = require('node:crypto');
const path = require('node:path');
const {targets} = require('./release-targets.cjs');
module.exports = async function publish({github, context}) {
  const sha = process.env.SOURCE_SHA;
  const tag = process.env.RELEASE_TAG;
  if (!/^[0-9a-f]{40}$/.test(sha) || !/^v0\.2\.0-alpha\.[1-9][0-9]{0,4}$/.test(tag)) throw new Error('Invalid release identity.');
  const branch = await github.rest.repos.getBranch({...context.repo, branch:'main'});
  if (branch.data.commit.sha !== sha) return false;
  const folder = path.resolve('artifacts/release');
  const manifest = JSON.parse(fs.readFileSync(path.join(folder, 'release.json'), 'utf8'));
  if (manifest.commit !== sha || manifest.channel !== 'alpha' || `v${manifest.serverVersion}` !== tag) throw new Error('Artifact provenance does not match the release.');
  const entries = fs.readFileSync(path.join(folder, 'SHA256SUMS'), 'utf8').trim().split('\n').map(line => {
    const match = /^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$/.exec(line);
    if (!match) throw new Error('Invalid checksum entry.');
    return {hash:match[1], name:match[2]};
  });
  if (entries.length !== 9 || new Set(entries.map(e=>e.name)).size !== 9) throw new Error('Incomplete or duplicate release assets.');
  for (const entry of entries) {
    const hash = crypto.createHash('sha256');
    for await (const chunk of fs.createReadStream(path.join(folder, entry.name))) hash.update(chunk);
    if (hash.digest('hex') !== entry.hash) throw new Error(`Checksum mismatch: ${entry.name}`);
  }
  // Verify the complete build set above before creating any draft. Each public
  // release contains only its platform assets and its own manifest/checksums.
  for (const target of targets(manifest.serverVersion, manifest.windowsClientVersion)) {
    for (const name of target.files) if (!entries.some(e => e.name === name)) throw new Error(`Missing platform asset: ${name}`);
  }
  for (const target of targets(manifest.serverVersion, manifest.windowsClientVersion)) {
    if ((await github.rest.repos.getBranch({...context.repo,branch:'main'})).data.commit.sha !== sha) return false;
    let release;
    try { release = (await github.rest.repos.getReleaseByTag({...context.repo,tag:target.tag})).data; }
    catch(error) { if(error.status !== 404) throw error; }
    if (release && release.target_commitish !== sha) throw new Error('Release tag source mismatch.');
    if (release && !release.draft) continue; // Already published platforms remain immutable on retries.
    const body = `Early alpha from commit ${sha}.\n\n${target.instructions}\n\nSee docs/deployment/RELEASES.md for setup. Builds are unsigned. Back up server data before upgrading. SHA256SUMS verifies download integrity.\n\nPlatform: ${target.id}; version: ${target.version}.${target.id === 'docker' ? ' Image: ' + manifest.image : ''}`;
    if (!release) release = (await github.rest.repos.createRelease({...context.repo,tag_name:target.tag,target_commitish:sha,name:`${target.name} ${target.version}`,body,draft:true,prerelease:true,make_latest:'false'})).data;
    const platformManifest = {commit:sha,platform:target.id,version:target.version,channel:'alpha',signed:false,...(target.id === 'docker' ? {image:manifest.image} : {})};
    const manifestData = Buffer.from(JSON.stringify(platformManifest,null,2)+'\n');
    const checksumData = Buffer.from([...target.files.map(name => {
      const entry = entries.find(e=>e.name===name);
      return `${entry.hash}  ${name}`;
    }),`${crypto.createHash('sha256').update(manifestData).digest('hex')}  release.json`].join('\n')+'\n');
    const existing = await github.paginate(github.rest.repos.listReleaseAssets,{...context.repo,release_id:release.id,per_page:100});
    const names = [...target.files,'release.json','SHA256SUMS'];
    // Remove obsolete assets only from retryable drafts, never a published release.
    for (const asset of existing) await github.rest.repos.deleteReleaseAsset({...context.repo,asset_id:asset.id});
    for (const name of names) {
      const data = name === 'release.json' ? manifestData : name === 'SHA256SUMS' ? checksumData : fs.readFileSync(path.join(folder,name));
      await github.rest.repos.uploadReleaseAsset({...context.repo,release_id:release.id,name,data,headers:{'content-type':'application/octet-stream','content-length':data.length}});
    }
    if ((await github.rest.repos.getBranch({...context.repo,branch:'main'})).data.commit.sha !== sha) return false;
    await github.rest.repos.updateRelease({...context.repo,release_id:release.id,draft:false,prerelease:true,body,make_latest:'false'});
  }
  return true;
};
