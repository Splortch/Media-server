const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('Android build installs the same stable SDK used by both modules', () => {
  const root = path.resolve(__dirname, '../..');
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/android-client.yml'), 'utf8');
  const packages = workflow.match(/^\s+packages: (.+)$/m)[1];
  const app = fs.readFileSync(path.join(root, 'clients/android/app/build.gradle.kts'), 'utf8');
  const compileSdk = app.match(/compileSdk = (\d+)/)[1];
  assert.ok(packages.split(' ').includes(`platforms;android-${compileSdk}.0`));
  assert.ok(packages.split(' ').includes('build-tools;36.0.0'));
  assert.ok(workflow.indexOf('packages:') < workflow.indexOf('run: ./gradlew'));
  for (const module of ['app', 'core']) {
    const build = fs.readFileSync(path.join(root, 'clients/android', module, 'build.gradle.kts'), 'utf8');
    assert.equal(build.match(/compileSdk = (\d+)/)[1], compileSdk);
    assert.match(build, /minSdk = 26/);
  }
  assert.match(app, /targetSdk = 36/);
});

test('built-in Kotlin and compiler plugins agree without suppressing compatibility checks', () => {
  const android = path.join(__dirname, '../../clients/android');
  const rootBuild = fs.readFileSync(path.join(android, 'build.gradle.kts'), 'utf8');
  const compiler = rootBuild.match(/kotlin-gradle-plugin:([\d.]+)/)[1];
  const versions = [...rootBuild.matchAll(/kotlin\("plugin\.(?:compose|serialization)"\) version "([\d.]+)"/g)].map(m => m[1]);
  assert.equal(versions.length, 2);
  assert.ok(versions.every(v => v === compiler));
  const agp = [...rootBuild.matchAll(/id\("com.android\.(?:application|library)"\) version "([\d.]+)"/g)].map(m => m[1]);
  assert.equal(agp.length, 2);
  assert.equal(agp[0], agp[1]);
  const app = fs.readFileSync(path.join(android, 'app/build.gradle.kts'), 'utf8');
  const composeBoms = [...app.matchAll(/androidx\.compose:compose-bom:([\d.]+)/g)].map(m => m[1]);
  assert.equal(composeBoms.length, 2);
  assert.equal(composeBoms[0], composeBoms[1]);
  const config = [rootBuild, fs.readFileSync(path.join(android, 'gradle.properties'), 'utf8'),
    ...['app', 'core'].map(module => fs.readFileSync(path.join(android, module, 'build.gradle.kts'), 'utf8'))].join('\n');
  assert.doesNotMatch(config, /kotlin\("android"\)|android\.(?:builtInKotlin|newDsl)\s*=\s*false/);
  assert.doesNotMatch(config, /suppressUnsupportedCompileSdk|suppressKotlinVersionCompatibilityCheck|skip-metadata-version-check/);
});

test('real-backend device tests cover the existing and new API levels before packaging', () => {
  const workflow = fs.readFileSync(path.join(__dirname, '../../.github/workflows/android-client.yml'), 'utf8');
  assert.match(workflow, /api-level: \['35', '37\.0'\]/);
  assert.match(workflow, /api-level: \$\{\{ matrix\.api-level \}\}/);
  assert.match(workflow, /script: python3 scripts\/test-android-backend.py/);
  assert.match(workflow, /fail-fast: false/);
  assert.match(workflow, /device:\n[\s\S]*needs: device_matrix\n\s+if: always\(\)/);
  assert.match(workflow, /DEVICE_RESULT: \$\{\{ needs\.device_matrix\.result \}\}/);
  assert.match(workflow, /run: test "\$DEVICE_RESULT" = success/);
});

test('headless device validation uses supported software graphics and explicit guest memory', () => {
  const workflow = fs.readFileSync(path.join(__dirname, '../../.github/workflows/android-client.yml'), 'utf8');
  const options = workflow.match(/^\s+emulator-options: (.+)$/m)[1];
  assert.match(options, /(?:^|\s)-gpu software(?:\s|$)/);
  assert.doesNotMatch(options, /swiftshader_indirect|angle_indirect|swangle_indirect/);
  assert.match(options, /(?:^|\s)-show-kernel(?:\s|$)/);
  assert.match(options, /(?:^|\s)-no-metrics(?:\s|$)/);
  assert.match(workflow, /ram-size: 2048M/);
  assert.match(workflow, /heap-size: 512M/);
  assert.match(workflow, /pre-emulator-launch-script: adb start-server/);
});

test('standalone Android publication is disabled', async()=>{
  await assert.rejects(require('../publish-android.cjs')({}), /Standalone Android publication is disabled/);
});

test('Android is packaged only after build and emulator tests, for the unified publisher', ()=>{
  const workflow = fs.readFileSync(path.join(__dirname,'../../.github/workflows/android-client.yml'),'utf8');
  assert.match(workflow,/workflow_call:/);
  assert.match(workflow,/needs: \[build, device\]/);
  assert.match(workflow,/name: release-android/);
  assert.doesNotMatch(workflow,/contents: write|publish-android/);
  assert.match(workflow,/inputs.build_number \|\| github.run_number/);
});
