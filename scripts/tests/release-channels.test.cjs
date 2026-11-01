'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const {parseVersion, channelFor, canPromote} = require('../release-channels.cjs');
const {targets} = require('../release-targets.cjs');

test('recognizes version channels and stable versions', () => {
  assert.equal(parseVersion('0.2.0-alpha.123', 'server').channel, 'alpha');
  assert.equal(parseVersion('0.2.0-beta.1', 'server').channel, 'beta');
  assert.equal(parseVersion('0.2.0', 'server').channel, 'stable');
  assert.equal(parseVersion('0.1.0-beta.1', 'client').sequence, 1);
});
test('matches platform channels and iteration numbers', () => {
  assert.equal(channelFor('0.2.0-alpha.12', '0.1.0-alpha.12'), 'alpha');
  assert.equal(channelFor('0.2.0-beta.1', '0.1.0-beta.1'), 'beta');
  assert.equal(channelFor('0.2.0', '0.1.0'), 'stable');
  assert.throws(() => channelFor('0.2.0-alpha.1', '0.1.0-beta.1'));
  assert.throws(() => channelFor('0.2.0-beta.1', '0.1.0-beta.2'));
});
test('rejects malformed versions', () => {
  for (const value of ['0.2.0-beta.0','0.2.0-alpha.0001','0.2.0-rc.1','0.2.0-beta.999999','0.2.0+metadata']) {
    assert.throws(() => parseVersion(value,'server'),value);
  }
});
test('allows only adjacent release promotions', () => {
  assert.equal(canPromote('alpha','beta'),true);
  assert.equal(canPromote('beta','stable'),true);
  assert.equal(canPromote('alpha','stable'),false);
  assert.equal(canPromote('stable','alpha'),false);
});
test('keeps platform-specific release tags and filenames', () => {
  for (const [server,client] of [['0.2.0-alpha.1','0.1.0-alpha.1'],['0.2.0-beta.1','0.1.0-beta.1'],['0.2.0','0.1.0']]) {
    const list = targets(server,client);
    assert.equal(list.length,7);
    assert.equal(new Set(list.map(t=>t.tag)).size,list.length);
    assert.ok(list.every(t=>t.files.length > 0));
  }
});

const {plan} = require('../release-plan.cjs');
function promotionHarness(channel,source_tag,beta_number='1') {
  const sha = 'a'.repeat(40);
  const github = {
    rest: {repos: {getReleaseByTag: async ({tag}) => {
      if (tag.includes('-alpha.') || tag.includes('-beta.')) return {data:{draft:false,prerelease:true,target_commitish:sha}};
      throw Object.assign(new Error('Missing release'),{status:404});
    },listReleases: async () => ({data:[]})}},
    paginate: async () => []
  };
  const context = {repo:{owner:'Splortch',repo:'Media-server'},eventName:'workflow_dispatch',
    ref:'refs/heads/main',sha:'b'.repeat(40),runNumber:500,
    payload:{inputs:{channel,source_tag,beta_number,publish:true}}};
  return {github,context,sha};
}
test('promotes a verified Alpha commit into a Beta version without using HEAD', async () => {
  const h = promotionHarness('beta','v0.2.0-alpha.12','1');
  const result = await plan(h);
  assert.equal(result.sha,h.sha);
  assert.equal(result.server,'0.2.0-beta.1');
  assert.equal(result.client,'0.1.0-beta.1');
  assert.equal(result.channel,'beta');
});
test('promotes a verified Beta commit to stable',async () => {
  const h = promotionHarness('stable','v0.2.0-beta.1');
  const result = await plan(h);
  assert.equal(result.sha,h.sha);
  assert.equal(result.server,'0.2.0');
  assert.equal(result.client,'0.1.0');
});
test('refuses invalid source channel and publishing without approval',async () => {
  const h = promotionHarness('beta','v0.2.0-beta.1');
  await assert.rejects(plan(h),/Incorrect source channel/);
  const other = promotionHarness('beta','v0.2.0-alpha.12');
  other.context.payload.inputs.publish=false;
  await assert.rejects(plan(other),/explicit approval/);
});
