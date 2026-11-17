const {test} = require('node:test');
const assert = require('node:assert/strict');
const {eligible, validateBundle, publish, WORKFLOWS} = require('../dependabot-locks.cjs');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const sha = 'a'.repeat(40);
const repo = {full_name: 'Splortch/Media-server'};
const run = {
  head_sha: sha, head_branch: 'dependabot/nuget/dotnet-platform',
  event: 'pull_request', conclusion: 'success', head_repository: repo,
};
function pr() {
  return {
    number: 193, state: 'open', user: {id: 49699333, login: 'dependabot[bot]'},
    head: {sha, ref: run.head_branch, repo}, base: {ref: 'main', repo},
  };
}
const lockPath = 'backend/locks/linux-x64/Api/packages.lock.json';
const content = JSON.stringify({version: 1, dependencies: {'net10.0': {}}});
const tree = [{path: lockPath, type: 'blob', mode: '100644'}];
function bundle() { return {pr: 193, sha, files: [{path: lockPath, content}]}; }

test('only the current same-repository Dependabot NuGet head is eligible', () => {
  assert.ok(eligible(pr(), run, repo.full_name));
  for (const alter of [
    x => x.user.id = 7, x => x.state = 'closed', x => x.head.sha = 'b'.repeat(40),
    x => x.head.repo = {full_name: 'attacker/fork'}, x => x.base.ref = 'release',
    x => x.head.ref = 'feature/random',
  ]) {
    const candidate = pr(); alter(candidate);
    assert.equal(eligible(candidate, run, repo.full_name), false);
  }
});

test('only existing regular NuGet JSON lockfiles can be published', () => {
  assert.equal(validateBundle(bundle(), run, pr(), tree)[0].path, lockPath);
  for (const name of ['.github/workflows/ci.yml', 'backend/src/../Api/packages.lock.json',
    'backend/src/New/packages.lock.json', '/backend/src/Api/packages.lock.json']) {
    const value = bundle(); value.files[0].path = name;
    assert.throws(() => validateBundle(value, run, pr(), tree));
  }
  const duplicate = bundle(); duplicate.files.push({...duplicate.files[0]});
  assert.throws(() => validateBundle(duplicate, run, pr(), tree));
  const stale = bundle(); stale.sha = 'b'.repeat(40);
  assert.throws(() => validateBundle(stale, run, pr(), tree));
  const invalid = bundle(); invalid.files[0].content = '{"version":1,"dependencies":[]}';
  assert.throws(() => validateBundle(invalid, run, pr(), tree));
  assert.throws(() => validateBundle(bundle(), run, pr(), [{...tree[0], mode: '120000'}]));
});

async function exercise(t, {advance = false, files = bundle().files, changed = null} = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dependabot-locks-'));
  t.after(() => fs.rmSync(dir, {recursive: true, force: true}));
  const bundlePath = path.join(dir, 'data.json');
  fs.writeFileSync(bundlePath, JSON.stringify({...bundle(), files}));
  const writes = [], dispatches = [];
  let reads = 0;
  const github = {
    paginate: async () => changed || [{filename: 'backend/src/Api/Api.csproj', status: 'modified'}],
    rest: {
      pulls: {
        get: async () => { const value = pr(); if (advance && ++reads > 1) value.head.sha = 'b'.repeat(40); return {data: value}; },
        listFiles: () => {},
      },
      git: {
        getCommit: async () => ({data: {tree: {sha: 'old-tree'}}}),
        getTree: async () => ({data: {tree, truncated: false}}),
        createTree: async x => { writes.push(x); return {data: {sha: 'new-tree'}}; },
        createCommit: async x => { writes.push(x); return {data: {sha: 'new-commit'}}; },
        updateRef: async x => { writes.push(x); },
      },
      actions: {createWorkflowDispatch: async x => {dispatches.push(x);}},
    },
  };
  await publish({
    github, context: {repo: {owner: 'Splortch', repo: 'Media-server'}, payload: {workflow_run: run}},
    core: {info: () => {}}, bundlePath,
  });
  return {writes, dispatches};
}

test('publishes one non-forced commit and validates its branch through every workflow', async t => {
  const {writes, dispatches} = await exercise(t);
  assert.deepEqual(writes[1].parents, [sha]);
  assert.equal(writes[2].force, false);
  assert.equal(writes[2].sha, 'new-commit');
  assert.deepEqual(dispatches.map(x => x.workflow_id), WORKFLOWS);
  assert.ok(dispatches.every(x => x.ref === run.head_branch));
});

test('a head change during publication never overwrites newer commits', async t => {
  const {writes, dispatches} = await exercise(t, {advance: true});
  assert.equal(writes.length, 2);
  assert.equal(dispatches.length, 0);
});

test('no-op repair creates no commits or duplicate validation runs', async t => {
  const {writes, dispatches} = await exercise(t, {files: []});
  assert.equal(writes.length, 0);
  assert.equal(dispatches.length, 0);
});

test('a workflow modification in the source PR prevents automatic writes', async t => {
  await assert.rejects(exercise(t, {changed: [
    {filename: 'backend/src/Api/Api.csproj', status: 'modified'},
    {filename: '.github/workflows/ci.yml', status: 'modified'},
  ]}), /limited to NuGet/);
});
