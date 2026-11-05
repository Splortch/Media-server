// GitHub metadata only: reuse published assets; never build or modify packages.
const {successfulRuns} = require('./release-plan.cjs');
const pattern = /^(?:(server-windows|server-linux|server-macos|client-windows|client-linux|client-macos|client-android|docker)\/)?v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta)\.([1-9]\d*))?$/;
function parse(tag) {
  const m = pattern.exec(tag);
  if (!m || m.slice(2, 5).concat(m[6] || []).some(n => !Number.isSafeInteger(Number(n))))
    throw new Error('Invalid release tag.');
  return {prefix:m[1] ? m[1]+'/' : '', base:m.slice(2,5).join('.'),
    numbers:m.slice(2,5).map(Number), channel:m[5] || 'stable', sequence:Number(m[6] || 0)};
}
function compare(a,b) {
  const x=[...a.numbers,{alpha:0,beta:1,stable:2}[a.channel],a.sequence];
  const y=[...b.numbers,{alpha:0,beta:1,stable:2}[b.channel],b.sequence];
  for(let i=0;i<x.length;i++) if(x[i]!==y[i]) return x[i]>y[i]?1:-1;
  return 0;
}
async function commitForTag(github, repo, tag) {
  let object=(await github.rest.git.getRef({...repo,ref:'tags/'+tag})).data.object;
  for(let i=0;object.type==='tag' && i<8;i++)
    object=(await github.rest.git.getTag({...repo,tag_sha:object.sha})).data.object;
  if(object.type!=='commit' || !/^[a-f0-9]{40}$/.test(object.sha))
    throw new Error('Tag must resolve to an immutable commit.');
  return object.sha;
}
async function promote({github,context,sourceTag,targetTag}) {
  if(context.eventName!=='workflow_dispatch' || context.ref!=='refs/heads/main')
    throw new Error('Promotion requires a maintainer dispatch on main.');
  const source=parse(sourceTag),target=parse(targetTag);
  if(source.prefix!==target.prefix || source.base!==target.base ||
    source.channel==='stable' || target.channel==='alpha' || compare(target,source)<=0)
    throw new Error('Promote within the same platform/base version, from Alpha to Beta/Stable or Beta to a later Beta/Stable.');
  const repo=context.repo, repository=repo.owner+'/'+repo.repo;
  const selected=(await github.rest.repos.getReleaseByTag({...repo,tag:sourceTag})).data;
  if(selected.draft || !selected.prerelease || selected.tag_name!==sourceTag)
    throw new Error('Source must be a published prerelease.');
  const sha=await commitForTag(github,repo,sourceTag);
  if(selected.target_commitish!==sha) throw new Error('Source release must name its exact commit SHA.');
  const runs=await github.paginate(github.rest.actions.listWorkflowRunsForRepo,
    {...repo,event:'push',head_sha:sha,per_page:100});
  if(!successfulRuns(runs,sha,repository)) throw new Error('Required exact-commit main CI checks have not passed.');
  if(source.prefix==='client-android/') {
    const android=runs.filter(r=>r.path==='.github/workflows/android-client.yml' &&
      r.head_sha===sha && r.event==='push' && r.head_branch==='main' &&
      r.repository?.full_name===repository).sort((a,b)=>b.id-a.id)[0];
    if(android?.status!=='completed' || android.conclusion!=='success')
      throw new Error('Exact-commit Android CI must pass before Android promotion.');
  }
  let original=selected;
  const marker=/<!-- release-promotion: (.+) -->/.exec(selected.body || '');
  if(marker) {
    const provenance=JSON.parse(marker[1]);
    const origin=parse(provenance.sourceTag);
    if(provenance.sha!==sha || origin.prefix!==source.prefix || origin.base!==source.base ||
      origin.channel!=='alpha') throw new Error('Invalid promotion provenance.');
    original=(await github.rest.repos.getReleaseByTag({...repo,tag:provenance.sourceTag})).data;
    if(original.draft || !original.prerelease || original.target_commitish!==sha ||
      await commitForTag(github,repo,provenance.sourceTag)!==sha)
      throw new Error('Original download release no longer matches selected commit.');
  }
  const assets=await github.paginate(github.rest.repos.listReleaseAssets,
    {...repo,release_id:original.id,per_page:100});
  if(assets.length<3 || !assets.some(a=>a.name==='release.json') ||
    !assets.some(a=>a.name==='SHA256SUMS') || new Set(assets.map(a=>a.name)).size!==assets.length ||
    assets.some(a=>a.state!=='uploaded' || !/^[A-Za-z0-9._-]+$/.test(a.name) ||
      !/^sha256:[a-f0-9]{64}$/.test(a.digest || '') || a.size<=0))
    throw new Error('Original release must have complete uploaded assets, manifest, checksums and SHA-256 digests.');
  const refs=await github.paginate(github.rest.git.listMatchingRefs,{...repo,ref:'tags/',per_page:100});
  for(const ref of refs) {
    let version;
    try { version=parse(ref.ref.replace(/^refs\/tags\//,'')); } catch { continue; }
    if(version.prefix===target.prefix && compare(version,target)>=0)
      throw new Error('Target must be unique and newer than existing tags for this platform.');
  }
  if(await commitForTag(github,repo,sourceTag)!==sha ||
    await commitForTag(github,repo,original.tag_name)!==sha)
    throw new Error('Source tag changed during promotion.');
  const root='https://github.com/'+repository+'/releases';
  const body=[
    '## '+(target.channel==='stable'?'Stable':'Beta')+' approval',
    '', 'Selected commit: '+sha+'.',
    '', 'Promoted from ['+sourceTag+']('+root+'/tag/'+encodeURIComponent(sourceTag)+').',
    '', '## Existing downloads',
    '', 'These links reuse the original published assets byte-for-byte. No installers, manifests, checksums, container tags or application versions were changed or rebuilt. Embedded versions and filenames retain their original Alpha build identity.',
    '', ...assets.map(a=>'- ['+a.name+']('+root+'/download/'+encodeURIComponent(original.tag_name)+'/'+encodeURIComponent(a.name)+') — '+a.digest),
    '', '<!-- release-promotion: '+JSON.stringify({sourceTag:original.tag_name,sha})+' -->'
  ].join('\n');
  // createRef rejects duplicates atomically; no existing tag/release is overwritten.
  await github.rest.git.createRef({...repo,ref:'refs/tags/'+targetTag,sha});
  const draft=(await github.rest.repos.createRelease({...repo,tag_name:targetTag,
    target_commitish:sha,name:targetTag,body,draft:true,prerelease:target.channel!=='stable',make_latest:'false'})).data;
  if(await commitForTag(github,repo,targetTag)!==sha ||
    await commitForTag(github,repo,sourceTag)!==sha ||
    await commitForTag(github,repo,original.tag_name)!==sha)
    throw new Error('A tag changed; leaving the promotion draft unpublished.');
  return (await github.rest.repos.updateRelease({...repo,release_id:draft.id,draft:false,
    prerelease:target.channel!=='stable',make_latest:'false'})).data;
}
module.exports={parse,compare,promote,commitForTag};
