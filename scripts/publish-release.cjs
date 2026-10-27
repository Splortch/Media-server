const fs = require('node:fs');
const crypto = require('node:crypto');
const path = require('node:path');
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
  let release;
  try { release = (await github.rest.repos.getReleaseByTag({...context.repo,tag})).data; }
  catch(error) { if(error.status !== 404) throw error; }
  if (release && release.target_commitish !== sha) throw new Error('Release tag source mismatch.');
  if (release && !release.draft) return true; // Published releases are immutable here.
  const body = `Early alpha from commit ${sha}.\n\nDownload the server for your OS, extract it, and use its Start launcher. The web client is included. The separate Windows client installers connect to a running server. Docker users can download the Compose setup. See the bundled README and docs/deployment/RELEASES.md.\n\nBuilds and installers are unsigned. No Android, Android TV, iOS or macOS app is included. SHA256SUMS verifies download integrity. Container package/license inventory and corresponding source are provided separately; ordinary installation does not require the source archive. Server data lives outside the versioned download folder. Back it up before updating.\n\nServer ${manifest.serverVersion}; Windows client ${manifest.windowsClientVersion}. Image: ${manifest.image}.`;
  if (!release) release = (await github.rest.repos.createRelease({...context.repo,tag_name:tag,target_commitish:sha,name:`Media Server ${manifest.serverVersion}`,body,draft:true,prerelease:true,make_latest:'false'})).data;
  const existing = await github.paginate(github.rest.repos.listReleaseAssets,{...context.repo,release_id:release.id,per_page:100});
  for (const name of [...entries.map(e=>e.name),'SHA256SUMS']) {
    const old = existing.find(asset=>asset.name===name);
    if (old) await github.rest.repos.deleteReleaseAsset({...context.repo,asset_id:old.id});
    const data = fs.readFileSync(path.join(folder,name));
    await github.rest.repos.uploadReleaseAsset({...context.repo,release_id:release.id,name,data,headers:{'content-type':'application/octet-stream','content-length':data.length}});
  }
  const current = await github.rest.repos.getBranch({...context.repo,branch:'main'});
  if (current.data.commit.sha !== sha) return false; // Leave an incomplete/superseded attempt private.
  await github.rest.repos.updateRelease({...context.repo,release_id:release.id,draft:false,prerelease:true,body,make_latest:'false'});
  return true;
};
