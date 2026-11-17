const fs = require('node:fs');

const WORKFLOWS = [
  'agent-automation-tests.yml', 'backend-server.yml', 'ci.yml', 'web-client.yml',
  'windows-client.yml', 'desktop-clients.yml', 'android-client.yml', 'playback-validation.yml',
];

function eligible(pr, run, repository) {
  return pr.state === 'open' && pr.user?.id === 49699333 &&
    pr.user?.login === 'dependabot[bot]' &&
    pr.head?.repo?.full_name === repository &&
    pr.base?.repo?.full_name === repository && pr.base?.ref === 'main' &&
    pr.head?.ref === run.head_branch && pr.head?.ref?.startsWith('dependabot/nuget/') &&
    pr.head?.sha === run.head_sha;
}

function validateBundle(bundle, run, pr, tree) {
  if (bundle.pr !== pr.number || bundle.sha !== run.head_sha || !Array.isArray(bundle.files)) {
    throw new Error('Artifact does not identify this PR head');
  }
  if (bundle.files.length > 100) throw new Error('Too many lockfiles');
  const allowed = new Set(tree.filter(x => x.type === 'blob' && x.mode === '100644').map(x => x.path));
  const seen = new Set();
  let bytes = 0;
  return bundle.files.map(file => {
    if (typeof file.path !== 'string' ||
        !/^backend\/(?:src|tests|tools|locks)\/(?:[A-Za-z0-9_.-]+\/)+packages\.lock\.json$/.test(file.path) ||
        file.path.split('/').some(x => x === '.' || x === '..') ||
        !allowed.has(file.path) || seen.has(file.path)) {
      throw new Error('Refusing unexpected lockfile path');
    }
    seen.add(file.path);
    if (typeof file.content !== 'string') throw new Error('Expected text');
    bytes += Buffer.byteLength(file.content);
    if (bytes > 10 * 1024 * 1024) throw new Error('Lock bundle too large');
    const parsed = JSON.parse(file.content);
    if (![1, 2].includes(parsed.version) || !parsed.dependencies ||
        typeof parsed.dependencies !== 'object' || Array.isArray(parsed.dependencies)) {
      throw new Error('Expected a NuGet lockfile');
    }
    return {path: file.path, content: file.content, type: 'blob', mode: '100644'};
  });
}

async function publish({github, context, core, bundlePath}) {
  const run = context.payload.workflow_run;
  const repository = context.repo.owner + '/' + context.repo.repo;
  if (run.event !== 'pull_request' || run.conclusion !== 'success' ||
      run.head_repository?.full_name !== repository ||
      !run.head_branch?.startsWith('dependabot/nuget/')) {
    throw new Error('Unexpected source workflow run');
  }
  if (fs.statSync(bundlePath).size > 12 * 1024 * 1024) throw new Error('Artifact too large');
  const bundle = JSON.parse(fs.readFileSync(bundlePath, 'utf8'));
  if (!Number.isSafeInteger(bundle.pr) || bundle.pr <= 0) throw new Error('Invalid PR number');
  const api = {...context.repo};
  const {data: pr} = await github.rest.pulls.get({...api, pull_number: bundle.pr});
  if (!eligible(pr, run, repository)) {
    core.info('PR was closed, rebased, or changed; discarding stale lockfiles.');
    return;
  }
  const changed = await github.paginate(github.rest.pulls.listFiles, {...api, pull_number: pr.number, per_page: 100});
  if (!changed.some(x => x.filename.endsWith('.csproj')) || changed.some(x =>
    x.status === 'removed' || x.status === 'renamed' ||
    !/^backend\/(?:src|tests|tools|locks)\/(?:[A-Za-z0-9_.-]+\/)+(?:[A-Za-z0-9_.-]+\.csproj|packages\.lock\.json)$/.test(x.filename) ||
    x.filename.split('/').some(part => part === '..' || part === '.'))) {
    throw new Error('Automatic repair is limited to NuGet manifest and lockfile PRs');
  }
  const {data: parent} = await github.rest.git.getCommit({...api, commit_sha: run.head_sha});
  const {data: tree} = await github.rest.git.getTree({...api, tree_sha: parent.tree.sha, recursive: 'true'});
  if (tree.truncated) throw new Error('Cannot validate a truncated tree');
  const entries = validateBundle(bundle, run, pr, tree.tree);
  if (!entries.length) {
    core.info('All NuGet lockfiles are already consistent.');
    return;
  }
  const {data: nextTree} = await github.rest.git.createTree({...api, base_tree: parent.tree.sha, tree: entries});
  if (nextTree.sha === parent.tree.sha) return;
  const {data: commit} = await github.rest.git.createCommit({
    ...api, message: 'build(deps): refresh consuming and platform NuGet locks',
    tree: nextTree.sha, parents: [run.head_sha],
  });
  // Check again after resolution/API calls. Non-forced update also rejects a race
  // between this read and the update: the new commit has only the old head as parent.
  const {data: current} = await github.rest.pulls.get({...api, pull_number: pr.number});
  if (!eligible(current, run, repository)) {
    core.info('PR advanced while preparing the commit; discarding it.');
    return;
  }
  await github.rest.git.updateRef({
    ...api, ref: 'heads/' + pr.head.ref, sha: commit.sha, force: false,
  });
  // GITHUB_TOKEN pushes do not reliably start PR checks. Explicit dispatches
  // validate the new branch head, with all normal locked restores and tests.
  const failures = [];
  for (const workflow_id of WORKFLOWS) {
    try {
      await github.rest.actions.createWorkflowDispatch({...api, workflow_id, ref: pr.head.ref});
    } catch (error) {
      failures.push(workflow_id + ': ' + error.message);
    }
  }
  if (failures.length) throw new Error('Locks committed; re-run these validations: ' + failures.join('; '));
  core.info('Updated lockfiles at ' + commit.sha + ' and dispatched all eight validation workflows.');
}

module.exports = {eligible, validateBundle, publish, WORKFLOWS};
