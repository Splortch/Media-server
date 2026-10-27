const required = ['backend-server.yml', 'web-client.yml', 'windows-client.yml', 'ci.yml', 'agent-automation-tests.yml'];
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
  const branch = await github.rest.repos.getBranch({ ...context.repo, branch: 'main' });
  if (branch.data.commit.sha !== sha) return { build: false, publish: false };
  const response = await github.rest.actions.listWorkflowRunsForRepo({ ...context.repo, event: 'push', head_sha: sha, per_page: 100 });
  const runs = successfulRuns(response.data.workflow_runs, sha, repository);
  if (!runs) return { build: false, publish: false };
  const windows = runs.find(r => r.path.endsWith('/windows-client.yml'));
  const release = { build: true, publish: true, sha, ...versions(windows.run_number) };
  try {
    const existing = await github.rest.repos.getReleaseByTag({ ...context.repo, tag: release.tag });
    if (existing.data.target_commitish !== sha) throw new Error('Existing release tag belongs to another source commit.');
    if (!existing.data.draft) return { build: false, publish: false };
  } catch (error) { if (error.status !== 404) throw error; }
  return release;
}
module.exports = { plan, successfulRuns, versions, required };
