const {test} = require('node:test');
const assert = require('node:assert/strict');
const {spawnSync} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const root = path.resolve(__dirname, '../..');
const vendor = path.join(root, 'vendor/picomatch');
const pm = require(vendor);

test('single-branch wildcard repeats cannot block a matcher process', () => {
  for (const pattern of ['+(*)b', '+(*a)', '*(*a)', '+(*?a)', 'a+(*b)c']) {
    const code = `const pm = require(${JSON.stringify(vendor)}); for (const options of [{}, {windows:true}, {maxExtglobRecursion:false}]) { if (pm(${JSON.stringify(pattern)}, options)('a'.repeat(80) + '!')) process.exit(2); }`;
    const result = spawnSync(process.execPath, ['-e', code], {timeout: 5000, encoding: 'utf8'});
    assert.equal(result.error, undefined, `${pattern}: matcher exceeded time budget`);
    assert.equal(result.status, 0, `${pattern}: ${result.stderr}`);
  }
});

test('ordinary glob, escaped-star, character-class and safe extglob behavior remains usable', () => {
  for (const [subject, pattern, expected] of [
    ['movie.mkv', '*.mkv', true], ['movie.mp4', '*.mkv', false],
    ['src/nested/file.ts', 'src/**/*.ts', true],
    ['abab', '+(ab)', true], ['aa', '+(ab)', false],
    ['***', '+([*])', true], ['***', '+(\\*)', true],
    ['foo.js', '*.{js,ts}', true], ['a.ts', '!(*.js)', true],
  ]) assert.equal(pm(pattern)(subject), expected, pattern);
  for (const pattern of ['+(*)b', '+(*a)', '*(*a)', '+(*?a)']) {
    assert.ok(pm(pattern)(pattern), 'unsafe patterns use upstream literal fallback');
  }
});

test('both npm lockfiles resolve every picomatch consumer to the reviewed local mitigation', () => {
  for (const dir of ['frontend', 'clients/windows']) {
    const manifest = JSON.parse(fs.readFileSync(path.join(root, dir, 'package.json'), 'utf8'));
    const lock = JSON.parse(fs.readFileSync(path.join(root, dir, 'package-lock.json'), 'utf8'));
    assert.equal(manifest.overrides.picomatch, '$picomatch');
    const entries = Object.entries(lock.packages).filter(([name]) => name.endsWith('node_modules/picomatch'));
    assert.equal(entries.length, 1, 'no unpatched nested copies');
    const [name, entry] = entries[0];
    assert.equal(name, 'node_modules/picomatch');
    assert.equal(entry.link, true);
    assert.equal(path.resolve(root, dir, entry.resolved), vendor);
    assert.equal(lock.packages[entry.resolved].version, '4.0.8-splortch.1');
    if (fs.existsSync(path.join(root, dir, 'node_modules/picomatch'))) {
      const requireClient = createRequire(path.join(root, dir, 'package.json'));
      assert.equal(fs.realpathSync(requireClient.resolve('picomatch')), path.join(vendor, 'index.js'));
    }
  }
});
