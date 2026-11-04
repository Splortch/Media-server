const {test} = require('node:test');
const assert = require('node:assert/strict');
const {parse,compare,promote} = require('../promote-release.cjs');
const {required} = require('../release-plan.cjs');
const sha='a'.repeat(40), sourceTag='server-windows/v0.2.0-alpha.123';
function fixture(targetTag='server-windows/v0.2.0-beta.1') {
  const state={writes:[], refs:[sourceTag], changed:false,
    releases:{[sourceTag]:{id:1,tag_name:sourceTag,target_commitish:sha,draft:false,prerelease:true}},
    assets:['release.json','SHA256SUMS','MediaServer-alpha.zip'].map((name,i)=>({id:i+1,name,state:'uploaded',size:100,digest:'sha256:'+'b'.repeat(64)})),
    runs:required.map((file,i)=>({id:i+1,path:'.github/workflows/'+file,head_sha:sha,event:'push',head_branch:'main',repository:{full_name:'Splortch/Media-server'},status:'completed',conclusion:'success'}))};
  const context={repo:{owner:'Splortch',repo:'Media-server'},eventName:'workflow_dispatch',ref:'refs/heads/main',sha:'c'.repeat(40)};
  const github={rest:{repos:{
    getReleaseByTag:async({tag})=>({data:state.releases[tag]}),
    listReleaseAssets:'assets',
    createRelease:async request=>{state.writes.push(request);return{data:{...request,id:2}};},
    updateRelease:async request=>{state.writes.push(request);return{data:request};},
  },actions:{listWorkflowRunsForRepo:'runs'},git:{
    getRef:async({ref})=>({data:{object:{type:state.annotated?'tag':'commit',sha:state.changed?'d'.repeat(40):sha}}}),
    getTag:async()=>({data:{object:{type:'commit',sha}}}),
    listMatchingRefs:'refs',
    createRef:async request=>{if(state.race)throw new Error('Reference already exists');state.writes.push(request);if(state.changeAfterCreate)state.changed=true;},
  }},paginate:async(method)=>method==='assets'?state.assets:method==='runs'?state.runs:state.refs.map(tag=>({ref:'refs/tags/'+tag}))};
  return {state,context,run:(source=sourceTag,target=targetTag)=>promote({github,context,sourceTag:source,targetTag:target})};
}
test('maps strict Alpha/Beta/Stable tags including platform prefixes',()=>{
  assert.equal(parse('v0.2.0').channel,'stable');
  assert.equal(parse('client-android/v0.1.0-beta.2').prefix,'client-android/');
  for(const bad of ['v01.2.0','v0.2.0-beta.0','v0.2.0-beta.01','v0.2.0-rc.1','v0.2.0+build','v0.2.0\n','main','v9007199254740992.0.0'])
    assert.throws(()=>parse(bad));
});
test('orders numeric counters and maturity correctly',()=>{
  for(const [a,b] of [['v0.2.0-alpha.999','v0.2.0-beta.1'],['v0.2.0-beta.9','v0.2.0-beta.10'],['v0.2.0-beta.20','v0.2.0'],['v0.2.0','v0.3.0-alpha.1']])
    assert.equal(compare(parse(a),parse(b)),-1);
});
for(const channel of ['beta.1','stable']) test('publishes '+channel+' at the selected older commit without changing assets',async()=>{
  const target='server-windows/v0.2.0'+(channel==='stable'?'':'-'+channel);
  const f=fixture(target);await f.run();
  assert.equal(f.state.writes.length,3);
  assert.equal(f.state.writes[0].sha,sha);
  const draft=f.state.writes[1],published=f.state.writes[2];
  assert.equal(draft.target_commitish,sha);assert.equal(draft.tag_name,target);assert.equal(draft.draft,true);
  assert.equal(published.prerelease,channel!=='stable');assert.equal(published.draft,false);
  assert.match(draft.body,/server-windows%2Fv0.2.0-alpha.123\/MediaServer-alpha.zip/);
  assert.match(draft.body,/retain their original Alpha build identity/);
  assert.equal(published.make_latest,'false');
});
test('resolves annotated source tags',async()=>{const f=fixture();f.state.annotated=true;await f.run();assert.equal(f.state.writes[0].sha,sha);});
for(const [name,edit] of [
  ['PR event',f=>f.context.eventName='pull_request'],
  ['non-main workflow',f=>f.context.ref='refs/heads/feature'],
  ['draft source',f=>f.state.releases[sourceTag].draft=true],
  ['normal source',f=>f.state.releases[sourceTag].prerelease=false],
  ['mutable commitish',f=>f.state.releases[sourceTag].target_commitish='main'],
  ['retagged source',f=>f.state.changed=true],
  ['missing workflow',f=>f.state.runs.pop()],
  ['wrong commit CI',f=>f.state.runs[0].head_sha='d'.repeat(40)],
  ['failed CI',f=>f.state.runs[0].conclusion='failure'],
  ['newer failed retry',f=>f.state.runs.push({...f.state.runs[0],id:99,conclusion:'failure'})],
  ['missing checksums',f=>f.state.assets=f.state.assets.filter(a=>a.name!=='SHA256SUMS')],
  ['incomplete upload',f=>f.state.assets[0].state='new'],
  ['missing digest',f=>delete f.state.assets[0].digest],
  ['duplicate asset',f=>f.state.assets.push(f.state.assets[0])],
  ['unsafe asset name',f=>f.state.assets[2].name='bad](url)'],
  ['existing target tag',f=>f.state.refs.push('server-windows/v0.2.0-beta.1')],
  ['newer release',f=>f.state.refs.push('server-windows/v0.3.0-alpha.1')],
]) test('rejects '+name+' before writing',async()=>{const f=fixture();edit(f);await assert.rejects(f.run());assert.equal(f.state.writes.length,0);});
for(const target of ['server-linux/v0.2.0-beta.1','server-windows/v0.3.0-beta.1','server-windows/v0.2.0-alpha.124','server-windows/v0.2.0-beta.01'])
  test('rejects invalid promotion to '+target,async()=>{const f=fixture(target);await assert.rejects(f.run());assert.equal(f.state.writes.length,0);});
test('does not overwrite a concurrently created target',async()=>{const f=fixture();f.state.race=true;await assert.rejects(f.run(),/already exists/);assert.equal(f.state.writes.length,0);});
test('leaves a draft unpublished if a tag changes during publication',async()=>{const f=fixture();f.state.changeAfterCreate=true;await assert.rejects(f.run(),/leaving.*draft/);assert.equal(f.state.writes.length,2);assert.equal(f.state.writes[1].draft,true);});
test('Beta to Stable retains the original Alpha download provenance',async()=>{
  const f=fixture(),beta='server-windows/v0.2.0-beta.1';
  f.state.releases[beta]={id:2,tag_name:beta,target_commitish:sha,draft:false,prerelease:true,
    body:'<!-- release-promotion: '+JSON.stringify({sourceTag,sha})+' -->'};
  f.state.refs.push(beta);await f.run(beta,'server-windows/v0.2.0');
  assert.match(f.state.writes[1].body,/download\/server-windows%2Fv0.2.0-alpha.123/);
});
test('rejects forged Beta provenance',async()=>{
  const f=fixture(),beta='server-windows/v0.2.0-beta.1';
  f.state.releases[beta]={id:2,tag_name:beta,target_commitish:sha,draft:false,prerelease:true,
    body:'<!-- release-promotion: '+JSON.stringify({sourceTag,sha:'e'.repeat(40)})+' -->'};
  await assert.rejects(f.run(beta,'server-windows/v0.2.0'),/provenance/);assert.equal(f.state.writes.length,0);
});
test('Android promotion additionally requires the exact Android workflow',async()=>{
  const f=fixture(),android='client-android/v0.1.0-alpha.123';
  f.state.releases[android]={...f.state.releases[sourceTag],tag_name:android};
  f.state.refs=[android];
  await assert.rejects(f.run(android,'client-android/v0.1.0-beta.1'),/Android CI/);
  assert.equal(f.state.writes.length,0);
  f.state.runs.push({...f.state.runs[0],id:100,path:'.github/workflows/android-client.yml'});
  await f.run(android,'client-android/v0.1.0-beta.1');
  assert.equal(f.state.writes[2].prerelease,true);
});
test('promotion workflow has a read-only PR test job and main-only manual write job',()=>{
  const fs=require('node:fs'),path=require('node:path');
  const workflow=fs.readFileSync(path.join(__dirname,'../../.github/workflows/promote-release.yml'),'utf8');
  assert.match(workflow,/if: github.event_name == 'workflow_dispatch' && github.ref == 'refs\/heads\/main'/);
  assert.match(workflow,/ref: \$\{\{ github.sha \}\}/);
  assert.match(workflow,/persist-credentials: false/);
  assert.doesNotMatch(workflow,/pull_request_target|workflow_run|packages: write|npm|dotnet|cargo|uploadReleaseAsset/);
  assert.match(workflow,/SOURCE_TAG: \$\{\{ inputs.source_tag \}\}/);
  assert.doesNotMatch(workflow,/run:.*inputs\./);
});
