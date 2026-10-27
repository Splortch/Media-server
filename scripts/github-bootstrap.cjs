const fs = require('node:fs');
module.exports = async ({github,context,core}, source = 'docs/roadmap.json') => {
 const {owner,repo}=context.repo; const scope={owner,repo};
 const roadmap=JSON.parse(fs.readFileSync(source,'utf8'));
 const definitions={bug:['d73a4a','A reproducible failure'],enhancement:['a2eeef','New or improved behavior'],security:['b60205','Security hardening; report vulnerabilities privately'],performance:['f9d0c4','Measured efficiency or playback work'],documentation:['0075ca','Documentation and contributor guidance'],frontend:['7057ff','Browser and client experience'],backend:['1d76db','Server, persistence and media pipeline'],'good first issue':['7057ff','Small and approachable starter work'],p0:['b60205','Must address before broader deployment'],p1:['fbca04','Next development priority'],p2:['c5def5','Later milestone']};
 const existingLabels=await github.paginate(github.rest.issues.listLabelsForRepo,{...scope,per_page:100});
 for(const [name,[color,description]] of Object.entries(definitions)) {
  if(!existingLabels.some(x=>x.name===name)) await github.rest.issues.createLabel({...scope,name,color,description});
 }
 const milestones=await github.paginate(github.rest.issues.listMilestones,{...scope,state:'all',per_page:100});
 for(const title of [...new Set(roadmap.map(x=>x.milestone))]) if(!milestones.some(x=>x.title===title)) milestones.push((await github.rest.issues.createMilestone({...scope,title,description:'Focused development milestone; no stable-release promise.'})).data);
 const existing=await github.paginate(github.rest.issues.listForRepo,{...scope,state:'all',per_page:100});const byKey={};const created=[];
 const body=(x)=>`<!-- roadmap:${x.key} -->
Priority: **${x.priority}**

${x.goal}

### Acceptance criteria
${x.acceptance.map(a=>'- [ ] '+a).join('\n')}

### Dependencies
${x.dependencies.length?x.dependencies.map(k=>byKey[k]?'#'+byKey[k].number:roadmap.find(i=>i.key===k).title).join(', '):'None; ready to scope.'}

### Suggested implementation
Keep the change within the current modular monolith. Add focused automated tests and update feature status. Split into child issues when implementation exceeds a reviewable PR.
`;
 for(const x of roadmap){const prior=existing.find(i=>!i.pull_request&&(i.body?.includes(`<!-- roadmap:${x.key} -->`)||i.title===x.title));if(prior){byKey[x.key]=prior;if(source==='docs/clients/roadmap.json') await github.rest.issues.update({...scope,issue_number:prior.number,milestone:milestones.find(m=>m.title===x.milestone).number,labels:x.labels,title:x.title});continue;}const issue=(await github.rest.issues.create({...scope,title:x.title,body:body(x),labels:x.labels,milestone:milestones.find(m=>m.title===x.milestone).number})).data;byKey[x.key]=issue;created.push(x);}
 for(const x of (source==='docs/clients/roadmap.json'?roadmap:created)) await github.rest.issues.update({...scope,issue_number:byKey[x.key].number,body:body(x)});
 core.notice(`Roadmap ready: ${roadmap.length} scoped issues (${created.length} newly created).`);
};
