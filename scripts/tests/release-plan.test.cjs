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
 for(let i=0;i<8;i++)fs.writeFileSync(path.join(folder,`artifact-${i}.zip`),'explicit unit test fixture');
 fs.writeFileSync(path.join(folder,'release.json'),JSON.stringify({commit:sha,channel:'alpha',serverVersion:'0.2.0-alpha.50',windowsClientVersion:'0.1.0-alpha.50',image:'test-image'}));
 const sums=fs.readdirSync(folder).sort().map(name=>`${crypto.createHash('sha256').update(fs.readFileSync(path.join(folder,name))).digest('hex')}  ${name}`).join('\n')+'\n';fs.writeFileSync(path.join(folder,'SHA256SUMS'),sums);
 const github={paginate:async()=>[],rest:{repos:{getBranch:async()=>({data:{commit:{sha}}}),getReleaseByTag:async()=>{throw Object.assign(new Error('not found'),{status:404});},createRelease:async p=>{assert.equal(p.draft,true);assert.equal(p.prerelease,true);created++;return{data:{id:1}};},listReleaseAssets:()=>{},uploadReleaseAsset:async()=>{uploads++;if(uploads===2)throw new Error('simulated network interruption');},updateRelease:async()=>{updated++;}}}};
 try {process.chdir(temporary);process.env.SOURCE_SHA=sha;process.env.RELEASE_TAG='v0.2.0-alpha.50';
 await assert.rejects(publish({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}}),/network interruption/);assert.equal(created,1);assert.equal(updated,0);
 fs.writeFileSync(path.join(folder,'artifact-0.zip'),'tampered');created=0;await assert.rejects(publish({github,context:{repo:{owner:'Splortch',repo:'Media-server'}}}),/Checksum mismatch/);assert.equal(created,0);
 }finally{process.chdir(original);for(const [key,value]of Object.entries(oldEnv)){if(value===undefined)delete process.env[key];else process.env[key]=value;}fs.rmSync(temporary,{recursive:true,force:true});}
});

test('automatic versions use the source Windows counter, not the coordinator counter',async()=>{const f=fixture();f.context.runNumber=70000;assert.equal((await f.run()).tag,'v0.2.0-alpha.50');});
