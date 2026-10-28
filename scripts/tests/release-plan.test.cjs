const {test} = require('node:test');
const assert = require('node:assert/strict');
const {plan, successfulRuns, versions, required} = require('../release-plan.cjs');
const {stamp} = require('../stamp-windows-version.cjs');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const sha = 'a'.repeat(40);
function fixture() {
  const context = {repo:{owner:'Splortch',repo:'Media-server'},sha,ref:'refs/heads/main',runNumber:100,eventName:'workflow_run',payload:{workflow_run:{event:'push',head_branch:'main',head_repository:{full_name:'Splortch/Media-server'},head_sha:sha,conclusion:'success'}}};
  const state = {head:sha,existing:null,runs:required.map((file,i)=>({id:i+1,path:`.github/workflows/${file}`,head_sha:sha,event:'push',head_branch:'main',repository:{full_name:'Splortch/Media-server'},status:'completed',conclusion:'success',run_number:50}))};
  const github = {rest:{repos:{getBranch:async()=>({data:{commit:{sha:state.head}}}),getReleaseByTag:async()=>{if(state.existing)return{data:state.existing};throw Object.assign(new Error('not found'),{status:404});}},actions:{listWorkflowRunsForRepo:async()=>({data:{workflow_runs:state.runs}})}}};
  return {context,state,run:()=>plan({github,context})};
}
test('all exact-commit main workflows authorize one alpha build with independent MSI version', async()=>{
  const f=fixture();const r=await f.run();assert.equal(r.publish,true);assert.equal(r.sha,sha);assert.equal(r.tag,'v0.2.0-alpha.50');assert.equal(r.client,'0.1.0-alpha.50');assert.equal(r.msi,'0.1.50');
});
for(const [label,edit] of [
  ['PR upstream run',f=>f.context.payload.workflow_run.event='pull_request'],
  ['fork run',f=>f.context.payload.workflow_run.head_repository.full_name='Other/Media-server'],
  ['failed upstream',f=>f.context.payload.workflow_run.conclusion='failure'],
  ['stale source',f=>f.state.head='b'.repeat(40)],
  ['missing workflow',f=>f.state.runs.pop()],
  ['pending workflow',f=>f.state.runs[0].status='in_progress'],
  ['failed workflow',f=>f.state.runs[0].conclusion='failure'],
  ['wrong workflow source',f=>f.state.runs[0].head_sha='b'.repeat(40)],
  ['wrong repository',f=>f.state.runs[0].repository.full_name='Other/Repo'],
  ['lookalike workflow name',f=>f.state.runs[0].path='.github/workflows/untrusted.yml'],
  ['published release',f=>f.state.existing={target_commitish:sha,draft:false}],
]) test(`does not publish ${label}`,async()=>{const f=fixture();edit(f);assert.equal((await f.run()).publish,false);});
test('newer failed attempt overrides older green results',async()=>{const f=fixture();f.state.runs.push({...f.state.runs[0],id:200,conclusion:'failure'});assert.equal((await f.run()).build,false);});
test('a same-source draft may be retried; another-source tag is rejected',async()=>{const f=fixture();f.state.existing={target_commitish:sha,draft:true};assert.equal((await f.run()).publish,true);f.state.existing.target_commitish='b'.repeat(40);await assert.rejects(f.run(),/another source/);});
test('PR and ordinary manual runs build without publishing',async()=>{const f=fixture();f.context.eventName='pull_request';assert.equal((await f.run()).publish,false);assert.equal((await f.run()).build,true);f.context.eventName='workflow_dispatch';f.context.payload={inputs:{publish:'false'}};assert.equal((await f.run()).publish,false);});
test('manual publication requires main and complete CI',async()=>{const f=fixture();f.context.eventName='workflow_dispatch';f.context.payload={inputs:{publish:true}};assert.equal((await f.run()).publish,true);f.context.ref='refs/heads/feature';assert.equal((await f.run()).publish,false);});
test('MSI build overflow is rejected instead of wrapping or reusing versions',()=>{for(const n of [0,-1,65536,1.5,NaN])assert.throws(()=>versions(n));assert.equal(versions(65535).msi,'0.1.65535');});
test('version stamp preserves dependency patches and updates actual app metadata',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'media-stamp-'));const dir=path.join(root,'clients/windows/src-tauri');fs.mkdirSync(dir,{recursive:true});
  fs.writeFileSync(path.join(dir,'tauri.conf.json'),JSON.stringify({version:'old',bundle:{windows:{wix:{version:'old'}}}}));
  fs.writeFileSync(path.join(dir,'Cargo.toml'),'[package]\nname = "media-server-windows-client"\nversion = "0.1.0-alpha.1"\n\n[patch.crates-io]\nglib = { git = "trusted", rev = "fixed" }\n');
  fs.writeFileSync(path.join(dir,'Cargo.lock'),'[[package]]\nname = "media-server-windows-client"\nversion = "0.1.0-alpha.1"\n');
  try{stamp(root,'0.1.0-alpha.123','0.1.123');const c=JSON.parse(fs.readFileSync(path.join(dir,'tauri.conf.json')));assert.equal(c.version,'0.1.0-alpha.123');assert.equal(c.bundle.windows.wix.version,'0.1.123');assert.match(fs.readFileSync(path.join(dir,'Cargo.toml'),'utf8'),/version = "0.1.0-alpha.123"/);assert.match(fs.readFileSync(path.join(dir,'Cargo.toml'),'utf8'),/rev = "fixed"/);assert.throws(()=>stamp(root,'0.1.0-alpha.65536','0.1.65536'));}finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('privileged publication is isolated from PR dry runs and uses current-run artifacts',()=>{
 const workflow=fs.readFileSync(path.join(__dirname,'../../.github/workflows/release.yml'),'utf8');
 assert.doesNotMatch(workflow,/pull_request_target|secrets: inherit/);
 assert.match(workflow,/if: needs.plan.outputs.publish == 'true'/);
 assert.match(workflow,/contents: write/);assert.match(workflow,/packages: write/);
 assert.doesNotMatch(workflow,/run-id:.*workflow_run/);
 assert.match(workflow,/ref: \$\{\{ needs.plan.outputs.sha \}\}/);
});

test('publisher verifies every byte before creating a release and keeps failed uploads private',async()=>{
 const publish=require('../publish-release.cjs');const crypto=require('node:crypto');const temporary=fs.mkdtempSync(path.join(os.tmpdir(),'media-publish-'));const original=process.cwd();const oldEnv={SOURCE_SHA:process.env.SOURCE_SHA,RELEASE_TAG:process.env.RELEASE_TAG};
 const folder=path.join(temporary,'artifacts/release');fs.mkdirSync(folder,{recursive:true});let created=0,updated=0,uploads=0;
 for(const target of require('../release-targets.cjs').targets('0.2.0-alpha.50','0.1.0-alpha.50'))for(const name of target.files)fs.writeFileSync(path.join(folder,name),'explicit unit test fixture');
 fs.writeFileSync(path.join(folder,'release.json'),JSON.stringify({commit:sha,channel:'alpha',serverVersion:'0.2.0-alpha.50',windowsClientVersion:'0.1.0-alpha.50',image:'test-image'}));
 const sums=fs.readdirSync(folder).sort().map(name=>`${crypto.createHash('sha256').update(fs.readFileSync(path.join(folder,name))).digest('hex')}  ${name}`).join('\n')+'\n';fs.writeFileSync(path.join(folder,'SHA256SUMS'),sums);
 const github={paginate:async()=>[],rest:{repos:{getBranch:async()=>({data:{commit:{sha}}}),getReleaseByTag:async()=>{throw Object.assign(new Error('not found'),{status:404});},createRelease:async p=>{assert.equal(p.draft,true);assert.equal(p.prerelease,true);created++;return{data:{id:1}};},listReleaseAssets:()=>{},uploadReleaseAsset:async()=>{uploads++;if(uploads===2)throw new Error('simulated network interruption');},updateRelease:async()=>{updated++;}}}};
 try {process.chdir(temporary);process.env.SOURCE_SHA=sha;process.env.RELEASE_TAG='v0.2.0-alpha.50';
 await assert.rejects(publish({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}}),/network interruption/);assert.equal(created,1);assert.equal(updated,0);
 fs.writeFileSync(path.join(folder,'MediaServer-v0.2.0-alpha.50-win-x64.zip'),'tampered');created=0;await assert.rejects(publish({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}}),/Checksum mismatch/);assert.equal(created,0);
 }finally{process.chdir(original);for(const [key,value]of Object.entries(oldEnv)){if(value===undefined)delete process.env[key];else process.env[key]=value;}fs.rmSync(temporary,{recursive:true,force:true});}
});

test('automatic versions use the source Windows counter, not the coordinator counter',async()=>{const f=fixture();f.context.runNumber=70000;assert.equal((await f.run()).tag,'v0.2.0-alpha.50');});

test('planner retries a partial platform release set',async()=>{
 const f=fixture();
 // The fixture returns one common existing value; a draft means the set is incomplete.
 f.state.existing={target_commitish:sha,draft:true};
 assert.equal((await f.run()).build,true);
});
test('platform release tags are unique and contain only relevant downloads',()=>{
 const all=require('../release-targets.cjs').targets('0.2.0-alpha.50','0.1.0-alpha.50');
 assert.deepEqual(all.map(t=>t.tag),['server-windows/v0.2.0-alpha.50','server-linux/v0.2.0-alpha.50','client-windows/v0.1.0-alpha.50','docker/v0.2.0-alpha.50']);
 assert.equal(new Set(all.flatMap(t=>t.files)).size,9);
 assert.equal(all[0].files.length,2);assert.equal(all[1].files.length,1);assert.equal(all[2].files.length,2);assert.equal(all[3].files.length,4);
});
test('publisher produces four releases with independent integrity manifests and resumes partial success',async()=>{
 const publish=require('../publish-release.cjs');const crypto=require('node:crypto');
 const temporary=fs.mkdtempSync(path.join(os.tmpdir(),'platform-publish-'));const original=process.cwd();
 const saved={SOURCE_SHA:process.env.SOURCE_SHA,RELEASE_TAG:process.env.RELEASE_TAG};
 const folder=path.join(temporary,'artifacts/release');fs.mkdirSync(folder,{recursive:true});
 const targets=require('../release-targets.cjs').targets('0.2.0-alpha.50','0.1.0-alpha.50');
 for(const target of targets)for(const name of target.files)fs.writeFileSync(path.join(folder,name),'fixture '+name);
 fs.writeFileSync(path.join(folder,'release.json'),JSON.stringify({commit:sha,channel:'alpha',serverVersion:'0.2.0-alpha.50',windowsClientVersion:'0.1.0-alpha.50',image:'test-image'}));
 fs.writeFileSync(path.join(folder,'SHA256SUMS'),fs.readdirSync(folder).sort().map(name=>crypto.createHash('sha256').update(fs.readFileSync(path.join(folder,name))).digest('hex')+'  '+name).join('\n')+'\n');
 const releases=new Map();const assets=new Map();let next=1,fail=true;
 const github={paginate:async(_fn,p)=>[...(assets.get(p.release_id)||[])].map(([name])=>({id:name,name})),rest:{repos:{
 getBranch:async()=>({data:{commit:{sha}}}),
 getReleaseByTag:async p=>{if(releases.has(p.tag))return{data:releases.get(p.tag)};throw Object.assign(new Error('missing'),{status:404});},
 createRelease:async p=>{const r={id:next++,target_commitish:p.target_commitish,draft:true};releases.set(p.tag_name,r);assets.set(r.id,new Map());return{data:r};},
 listReleaseAssets:()=>{},deleteReleaseAsset:async p=>{for(const a of assets.values())a.delete(p.asset_id);},
 uploadReleaseAsset:async p=>{if(p.release_id===2&&fail){fail=false;throw new Error('interrupted second platform');}assets.get(p.release_id).set(p.name,Buffer.from(p.data));},
 updateRelease:async p=>{for(const r of releases.values())if(r.id===p.release_id)r.draft=p.draft;}
 }}};
 try{process.chdir(temporary);process.env.SOURCE_SHA=sha;process.env.RELEASE_TAG='v0.2.0-alpha.50';
 await assert.rejects(publish({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}}),/second platform/);
 assert.equal(releases.get(targets[0].tag).draft,false);assert.equal(releases.get(targets[1].tag).draft,true);
 assert.equal(await publish({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}}),true);assert.equal(releases.size,4);
 for(const target of targets){
  const r=releases.get(target.tag);assert.equal(r.draft,false);const a=assets.get(r.id);
  assert.deepEqual([...a.keys()].sort(),[...target.files,'release.json','SHA256SUMS'].sort());
  assert.equal(JSON.parse(a.get('release.json')).platform,target.id);
  for(const line of a.get('SHA256SUMS').toString().trim().split('\n')){
   const [hash,name]=line.split('  ');assert.equal(crypto.createHash('sha256').update(a.get(name)).digest('hex'),hash);
  }
 }
 }finally{process.chdir(original);for(const [k,v]of Object.entries(saved)){if(v===undefined)delete process.env[k];else process.env[k]=v;}fs.rmSync(temporary,{recursive:true,force:true});}
});

test('release descriptions explain each real platform and only synchronize recognized alpha notes',async()=>{
 const {platformNotes,syncReleaseNotes}=require('../release-notes.cjs');
 const targets=require('../release-targets.cjs').targets('0.2.0-alpha.50','0.1.0-alpha.50');
 const bodies=targets.map(t=>platformNotes(t,sha,'Splortch/Media-server','test-image'));
 assert.match(bodies[0],/Start-MediaServer.cmd/);assert.match(bodies[1],/sudo apt install ffmpeg/);
 assert.match(bodies[2],/WebView2/);assert.match(bodies[3],/docker compose up -d/);
 const changed=[];
 const releases=[
 {id:1,tag_name:'v0.2.0-alpha.40',target_commitish:sha,prerelease:true,draft:false},
 ...targets.map((t,i)=>({id:i+2,tag_name:t.tag,target_commitish:sha,prerelease:true,draft:false})),
 {id:6,tag_name:'server-linux/v0.2.0-alpha.50',target_commitish:sha,prerelease:true,draft:true},
 {id:7,tag_name:'server-linux/v0.2.0-alpha.50',target_commitish:sha,prerelease:false,draft:false},
 {id:8,tag_name:'unrelated',target_commitish:sha,prerelease:true,draft:false}
 ];
 const github={paginate:async()=>releases,rest:{repos:{listReleases:()=>{},updateRelease:async p=>changed.push(p)}}};
 await syncReleaseNotes({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}});
 assert.deepEqual(changed.map(p=>p.release_id),[1,2,3,4,5]);
 for(const p of changed){assert.deepEqual(Object.keys(p).sort(),['body','owner','release_id','repo']);assert.match(p.body,/Get started/);}
});

test('server installer notes are limited to releases containing that actual asset',()=>{
 const {platformNotes}=require('../release-notes.cjs');const target=require('../release-targets.cjs').targets('0.2.0-alpha.50','0.1.0-alpha.50')[0];
 assert.doesNotMatch(platformNotes(target,sha,'Splortch/Media-server','image'), /win-x64-setup\.exe/);
 const notes=platformNotes(target,sha,'Splortch/Media-server','image',{serverInstaller:true});
 assert.match(notes,/MediaServer-v0\.2\.0-alpha\.50-win-x64-setup\.exe/);assert.match(notes,/tray controller/);
});
