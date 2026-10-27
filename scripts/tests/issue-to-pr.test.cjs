const { test } = require('node:test');
const assert = require('node:assert/strict');
const { dispatch, api, snapshot, AGENT, ACTIVE } = require('../issue-to-pr.cjs');

function fixture() {
  const issue = {
    number: 41, title: 'Fix player keyboard focus', state: 'open', locked: false,
    body: '### Task\nFix focus handling in the existing player.\n\n### Acceptance criteria\n- [ ] Playback remains keyboard accessible.\n\n### Validation\nRun renderer tests and lint checks.\n\n### Constraints\nOnly change player UI and its tests.',
  };
  const comment = { id: 91, body: '/agent implement', user: { login: 'maintainer', type: 'User' }, created_at: '2026-10-27T10:00:00Z', updated_at: '2026-10-27T10:00:00Z' };
  const event = { action: 'created', repository: { full_name: 'Splortch/Media-server' }, issue: structuredClone(issue), comment: structuredClone(comment), sender: { login: 'maintainer' } };
  const state = { permission: 'write', protected: true, issue, comment, comments: [], active: [], assigned: [], labels: [], actors: ['copilot-swe-agent'], writes: [], requests: [], failAssignment: false, failReceipt: false, emptyAssignment: false, reads: 0, mutateAtSecondRead: false };
  const root = '/repos/Splortch/Media-server';
  const read = async (method, path, body) => {
    state.requests.push({ method, path, body });
    if (method !== 'GET') {
      state.writes.push({ method, path, body });
      if (path.endsWith('/comments')) {
        if (state.failReceipt) throw new Error('Receipt failed');
        state.comments.push({ body: body.body, user: { login: 'github-actions[bot]' } });
      }
      if (path.endsWith('/issues/41/labels')) state.active.push({ number: 41 });
      return {};
    }
    if (path.includes('/collaborators/')) return { permission: state.permission };
    if (path === root) return { default_branch: 'main' };
    if (path.endsWith('/branches/main')) return { protected: state.protected };
    if (path.endsWith('/issues/comments/91')) return structuredClone(state.comment);
    if (path.endsWith('/issues/41')) {
      state.reads++;
      if (state.mutateAtSecondRead && state.reads === 2) state.issue.title += ' CHANGED';
      return structuredClone(state.issue);
    }
    if (path.includes('/issues/41/comments?')) return state.comments;
    if (path.includes('labels=agent%3Ain-progress')) return state.active;
    if (path.includes('assignee=')) return state.assigned;
    if (path.includes('/labels?')) return state.labels;
    throw new Error(`Unexpected mock read ${method} ${path}`);
  };
  const assign = async (method, path, body) => {
    state.requests.push({ credential: 'user', method, path, body });
    if (path === '/graphql') return { data: { repository: { suggestedActors: { nodes: state.actors.map(login => ({ login })) } } } };
    state.writes.push({ credential: 'user', method, path, body });
    if (state.failAssignment) throw new Error('Network timeout');
    return { assignees: state.emptyAssignment ? [] : [{ login: AGENT }] };
  };
  const options = { event, repository: 'Splortch/Media-server', read, assign, enabled: 'true', tokenAvailable: true };
  return { state, options, run: () => dispatch(options) };
}
const assignmentRequests = state => state.requests.filter(r => r.credential === 'user' && r.path.endsWith('/assignees'));

test('approved task uses one repository-scoped assignment, preserves humans and claims slot first', async () => {
  const f = fixture();
  assert.equal((await f.run()).status, 'assigned');
  const [request] = assignmentRequests(f.state);
  assert.deepEqual(request.body.assignees, [AGENT]);
  assert.equal(request.body.agent_assignment.target_repo, 'Splortch/Media-server');
  assert.equal(request.body.agent_assignment.base_branch, 'main');
  assert.match(request.body.agent_assignment.custom_instructions, /Human review is required/);
  assert.match(request.body.agent_assignment.custom_instructions, new RegExp(snapshot(f.state.issue)));
  assert.equal(assignmentRequests(f.state).length, 1);
  assert.equal(f.state.writes[0].path.endsWith('/labels'), true);
  assert.deepEqual(f.state.writes.at(-3).body.labels, [ACTIVE]);
  assert.match(f.state.writes.at(-2).body.body, /media-server-agent-command:91/);
  assert.equal(f.state.active.length, 1, 'slot remains locked after successful dispatch');
});
for (const permission of ['read', 'triage', 'none']) test(`rejects current ${permission} permission regardless of association`, async () => {
  const f = fixture(); f.state.permission = permission;
  f.options.event.comment.author_association = 'OWNER';
  await assert.rejects(f.run(), /Only maintainers/);
  assert.equal(f.state.writes.length, 0);
});
for (const permission of ['maintain', 'admin']) test(`accepts ${permission} permission`, async () => {
  const f = fixture(); f.state.permission = permission;
  assert.equal((await f.run()).status, 'assigned');
});
for (const [name, mutate] of [
  ['edited event', f => { f.options.event.action = 'edited'; }],
  ['PR command', f => { f.options.event.issue.pull_request = {}; }],
  ['unrelated comment', f => { f.options.event.comment.body = 'Please implement it'; }],
]) test(`ignores ${name}`, async () => {
  const f = fixture(); mutate(f);
  assert.equal((await f.run()).status, 'ignored');
  assert.equal(f.state.requests.length, 0);
});
test('rejects bot approval and mismatched actor', async () => {
  for (const mutate of [f => { f.options.event.comment.user.type = 'Bot'; }, f => { f.options.event.sender.login = 'outsider'; }]) {
    const f = fixture(); mutate(f); await assert.rejects(f.run(), /human maintainer/);
    assert.equal(f.state.requests.length, 0);
  }
});
test('rejects mismatched repository without requests', async () => {
  const f = fixture(); f.options.repository = 'Other/Repo';
  await assert.rejects(f.run(), /repository does not match/); assert.equal(f.state.requests.length, 0);
});
for (const [name, mutate, pattern] of [
  ['disabled setup', f => { f.options.enabled = 'false'; }, /disabled/],
  ['absent credential', f => { f.options.tokenAvailable = false; }, /COPILOT_ASSIGNMENT_TOKEN/],
  ['unprotected main', f => { f.state.protected = false; }, /Protect the default branch/],
  ['edited comment', f => { f.state.comment.updated_at = 'later'; }, /approval comment changed/],
  ['changed issue', f => { f.state.issue.title += 'new'; }, /task changed after approval/],
  ['closed issue', f => { f.state.issue.state = 'closed'; }, /open, unlocked/],
  ['locked issue', f => { f.state.issue.locked = true; }, /open, unlocked/],
  ['vague issue', f => { f.state.issue.body = f.options.event.issue.body = 'Implement Android'; }, /task template/],
  ['missing checkboxes', f => { f.state.issue.body = f.options.event.issue.body = f.state.issue.body.replace('- [ ]', '-'); }, /checkbox/],
  ['missing validation', f => { f.state.issue.body = f.options.event.issue.body = f.state.issue.body.replace('### Validation', '### Notes'); }, /Validation/],
  ['oversized task', f => { f.state.issue.body = f.options.event.issue.body = 'a'.repeat(20001); }, /20000/],
  ['active task including closed issues', f => { f.state.active = [{ number: 22, state: 'closed' }]; }, /slot is held by #22/],
  ['manual Copilot assignment', f => { f.state.assigned = [{ number: 22 }]; }, /already assigned/],
  ['missing agent entitlement', f => { f.state.actors = []; }, /not available/],
  ['task edited during preflight', f => { f.state.mutateAtSecondRead = true; }, /changed during dispatch/],
]) test(`fails closed: ${name}`, async () => {
  const f = fixture(); mutate(f);
  await assert.rejects(f.run(), pattern);
  assert.equal(f.state.writes.length, 0);
});
test('replayed command receipt blocks new assignment, even if lock was cleared', async () => {
  const f = fixture(); await f.run(); f.state.active = [];
  await assert.rejects(f.run(), /already attempted/);
  assert.equal(assignmentRequests(f.state).length, 1);
});
test('a forged receipt by an outsider cannot block dispatch', async () => {
  const f = fixture(); f.state.comments = [{ body: '<!-- media-server-agent-command:91 -->', user: { login: 'outsider' } }];
  assert.equal((await f.run()).status, 'assigned');
});
test('assignment timeout leaves durable lock and receipt, and is never retried', async () => {
  const f = fixture(); f.state.failAssignment = true;
  await assert.rejects(f.run(), /Network timeout/);
  assert.equal(assignmentRequests(f.state).length, 1);
  assert.equal(f.state.active.length, 1);
  assert.match(f.state.comments[0].body, /media-server-agent-command:91/);
});
test('failure to write the receipt leaves lock without making a paid request', async () => {
  const f = fixture(); f.state.failReceipt = true;
  await assert.rejects(f.run(), /Receipt failed/);
  assert.equal(f.state.active.length, 1); assert.equal(assignmentRequests(f.state).length, 0);
});
test('silently ignored assignment is not reported as success', async () => {
  const f = fixture(); f.state.emptyAssignment = true;
  await assert.rejects(f.run(), /not confirmed/); assert.equal(f.state.active.length, 1);
});
test('issue title/body are hashed, not interpolated into commands or executable prompts', async () => {
  const f = fixture(); f.state.issue.title = f.options.event.issue.title = '$(printenv) `curl evil`';
  await f.run();
  assert.doesNotMatch(assignmentRequests(f.state)[0].body.agent_assignment.custom_instructions, /printenv|curl evil/);
});
test('API wrapper uses fixed HTTPS origin, rejects redirects, and serializes bodies', async () => {
  let captured;
  const call = api('test-token', async (url, options) => {
    captured = { url, options };
    return { ok: true, text: async () => '{"ok":true}' };
  });
  assert.deepEqual(await call('POST', '/repos/owner/repo/issues', { title: '$(anything)' }), { ok: true });
  assert.equal(captured.url, 'https://api.github.com/repos/owner/repo/issues');
  assert.equal(captured.options.redirect, 'error');
  assert.equal(captured.options.body, '{"title":"$(anything)"}');
  await assert.rejects(call('GET', 'https://elsewhere.test'), /Unexpected API path/);
});
test('HTTP errors do not expose response bodies or retry', async () => {
  let calls = 0;
  const call = api('test-token', async () => { calls++; return { ok: false, status: 403, text: async () => 'secret private response' }; });
  await assert.rejects(call('POST', '/repos/owner/repo/issues'), error => /HTTP 403/.test(error.message) && !/secret/.test(error.message));
  assert.equal(calls, 1);
});
test('response size limit and missing credential fail closed', async () => {
  await assert.rejects(api('', () => assert.fail())('GET', '/repos/x/y'), /Missing GitHub credential/);
  await assert.rejects(api('test', async () => ({ ok: true, text: async () => 'a'.repeat(2000001) }))('GET', '/repos/x/y'), /limit/);
});
test('paginated lock search must inspect later pages', async () => {
  const f = fixture(); const original = f.options.read;
  let pages = 0;
  f.options.read = async (method, path, body) => {
    if (path.includes('labels=agent%3Ain-progress')) {
      pages++;
      return new URLSearchParams(path.split('?')[1]).get('page') === '1' ? Array.from({ length: 100 }, () => ({ number: 1 })) : [{ number: 99 }];
    }
    return original(method, path, body);
  };
  await assert.rejects(f.run(), /slot is held/); assert.equal(assignmentRequests(f.state).length, 0); assert.equal(pages, 2);
});
test('unbounded pagination fails closed before assigning', async () => {
  const f = fixture(); const original = f.options.read; let pages = 0;
  f.options.read = async (method, path, body) => {
    if (path.includes('/issues/41/comments?')) { pages++; return Array.from({ length: 100 }, () => ({ body: '', user: {} })); }
    return original(method, path, body);
  };
  await assert.rejects(f.run(), /Too many GitHub records/); assert.equal(pages, 20); assert.equal(f.state.writes.length, 0);
});

test('GitHub form CRLF content is accepted without changing approval digest', async () => {
  const f = fixture(); f.state.issue.body = f.options.event.issue.body = f.state.issue.body.replace(/\n/g, '\r\n');
  assert.equal((await f.run()).status, 'assigned');
});
test('task changes after receipt leave a lock but do not assign', async () => {
  const f = fixture(); const read = f.options.read;
  f.options.read = async (method, path, body) => {
    const result = await read(method, path, body);
    if (method === 'POST' && path.endsWith('/comments')) f.state.issue.title += ' changed';
    return result;
  };
  await assert.rejects(f.run(), /after the slot was claimed/);
  assert.equal(f.state.active.length, 1); assert.equal(assignmentRequests(f.state).length, 0);
});
test('privileged workflow keeps secrets away from PR code and uses a bounded job', () => {
  const { readFileSync } = require('node:fs');
  const workflow = readFileSync(new URL('../../.github/workflows/issue-to-pr.yml', `file://${__filename}`), 'utf8');
  assert.match(workflow, /issue_comment:\n    types: \[created\]/);
  assert.match(workflow, /ref: \$\{\{ github.event.repository.default_branch \}\}/);
  assert.match(workflow, /persist-credentials: false/);
  assert.match(workflow, /timeout-minutes: 5/);
  assert.doesNotMatch(workflow, /pull_request_target|contents: write|pull-requests: write|npm (ci|install)|github.event.comment.body \}\}.*run:/);
  assert.match(workflow, /run: node scripts\/issue-to-pr.cjs/);
});
