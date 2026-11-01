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
