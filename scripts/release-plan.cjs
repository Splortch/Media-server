const {targets} = require('./release-targets.cjs');
const {parseVersion,canPromote} = require('./release-channels.cjs');
const required = ['backend-server.yml', 'web-client.yml', 'windows-client.yml', 'ci.yml', 'agent-automation-tests.yml', 'desktop-clients.yml'];
function versions(number) {
  if (!Number.isSafeInteger(number) || number < 1 || number > 65535) throw new Error('Release build number must fit the Windows installer version (1..65535).');
  return { server: `0.2.0-alpha.${number}`, client: `0.1.0-alpha.${number}`, msi: `0.1.${number}`, tag: `v0.2.0-alpha.${number}` };
}
function successfulRuns(runs, sha, repository) {
  const selected = required.map(file => runs.filter(r =>
    r.path === `.github/workflows/${file}` && r.head_sha === sha && r.event === 'push' &&
    r.head_branch === 'main' && r.repository?.full_name === repository
  ).sort((a, b) => b.id - a.id)[0]);
  return selected.every(r => r?.status === 'completed' && r.conclusion === 'success') ? selected : null;
}
async function plan({ github, context }) {
  const repository = `${context.repo.owner}/${context.repo.repo}`;
  const dry = () => ({ build: true, publish: false, sha: context.sha, ...versions(context.runNumber) });
  if (context.eventName === 'pull_request') return dry();
  const inputs = context.payload.inputs || {};
  if (context.eventName === 'workflow_dispatch' && ['beta','stable'].includes(inputs.channel)) {
    if (context.ref !== 'refs/heads/main' || ![true,'true'].includes(inputs.publish)) throw new Error('Promotion requires main and explicit approval');
    const from = inputs.channel === 'beta' ? 'alpha' : 'beta';
    if (!canPromote(from,inputs.channel)) throw new Error('Invalid promotion');
    const source = String(inputs.source_tag || '').replace(/^v/, '');
    const sourceInfo = parseVersion(source,'server');
    if (sourceInfo.channel !== from) throw new Error('Incorrect source channel');
    const sequence = Number(inputs.beta_number);
    if (inputs.channel === 'beta' && (!Number.isInteger(sequence) || sequence < 1 || sequence > 65535)) throw new Error('Invalid beta sequence');
    const sourceClient = '0.1.0-' + from + '.' + sourceInfo.sequence;
    const sourceTargets = targets(source,sourceClient);
    const origin = await github.rest.repos.getReleaseByTag({...context.repo,tag:sourceTargets[0].tag});
    const sha = origin.data.target_commitish;
    if (!/^[a-f0-9]{40}$/.test(sha)) throw new Error('Immutable source SHA required');
    for (const target of sourceTargets) {
      const rel = await github.rest.repos.getReleaseByTag({...context.repo,tag:target.tag});
      if (rel.data.draft || !rel.data.prerelease || rel.data.target_commitish !== sha) throw new Error('Source releases are incomplete or inconsistent');
    }
    const server = inputs.channel === 'beta' ? '0.2.0-beta.' + sequence : '0.2.0';
    const client = inputs.channel === 'beta' ? '0.1.0-beta.' + sequence : '0.1.0';
    if (inputs.channel === 'beta') {
      const releases = await github.paginate(github.rest.repos.listReleases,{...context.repo,per_page:100});
      const existingNumbers = releases.map(item => /^server-windows\/v0\.2\.0-beta\.([1-9]\d{0,4})$/.exec(item.tag_name))
        .filter(Boolean).map(match => Number(match[1]));
      if (existingNumbers.some(number => number >= sequence)) throw new Error('Beta sequence must increase monotonically');
    }
    const planned = {build:true,publish:true,sha,server,client,msi:'0.1.'+context.runNumber,tag:'v'+server,channel:inputs.channel,sourceTag:'v'+source};
    for (const target of targets(server,client)) {
      try { await github.rest.repos.getReleaseByTag({...context.repo,tag:target.tag}); throw new Error('Destination already exists: '+target.tag); }
      catch(error) { if (error.status !== 404) throw error; }
    }
    return planned;
  }
  if (context.eventName === 'workflow_dispatch' && ![true, 'true'].includes(context.payload.inputs?.publish)) return dry();
  let sha;
  if (context.eventName === 'workflow_run') {
    const run = context.payload.workflow_run;
    if (run.event !== 'push' || run.head_branch !== 'main' || run.head_repository?.full_name !== repository || run.conclusion !== 'success') return { build: false, publish: false };
    sha = run.head_sha;
  } else if (context.eventName === 'workflow_dispatch' && context.ref === 'refs/heads/main') {
    sha = context.sha;
  } else return { build: false, publish: false };
  if (!/^[a-f0-9]{40}$/.test(sha)) throw new Error('Invalid source commit.');
  // Builds always check out the workflow's own immutable event commit. Never
  // execute a planner-supplied ref in the default branch's cache/security context.
  if (sha !== context.sha) return { build: false, publish: false };
  const branch = await github.rest.repos.getBranch({ ...context.repo, branch: 'main' });
  if (branch.data.commit.sha !== sha) return { build: false, publish: false };
  const response = await github.rest.actions.listWorkflowRunsForRepo({ ...context.repo, event: 'push', head_sha: sha, per_page: 100 });
  const runs = successfulRuns(response.data.workflow_runs, sha, repository);
  if (!runs) return { build: false, publish: false };
  const windows = runs.find(r => r.path.endsWith('/windows-client.yml'));
  const release = { build: true, publish: true, sha, ...versions(windows.run_number) };
  let complete = true;
  for (const target of targets(release.server, release.client)) {
    try {
      const existing = await github.rest.repos.getReleaseByTag({ ...context.repo, tag: target.tag });
      if (existing.data.target_commitish !== sha) throw new Error('Existing release tag belongs to another source commit.');
      if (existing.data.draft) complete = false;
    } catch (error) { if (error.status !== 404) throw error; complete = false; }
  }
  if (complete) return { build: false, publish: false };
  return release;
}
module.exports = { plan, successfulRuns, versions, required };
