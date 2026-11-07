const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('Android preview SDK is installed from the preview channel before the build', () => {
  const root = path.resolve(__dirname, '../..');
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/android-client.yml'), 'utf8');
  const packages = workflow.match(/^\\s+packages: (.+)$/m)[1];
  assert.doesNotMatch(packages, /platforms;android-37(?:\\s|$)/);
  const previewInstall = workflow.indexOf('sdkmanager --install --channel=3 "platforms;android-37.0"');
  assert.ok(previewInstall > 0);
  assert.ok(previewInstall < workflow.indexOf('run: ./gradlew'));
  for (const module of ['app', 'core']) {
    const build = fs.readFileSync(path.join(root, 'clients/android', module, 'build.gradle.kts'), 'utf8');
    assert.match(build, /compileSdk = 37/);
  }
});

test('standalone Android publication is disabled', async()=>{
  await assert.rejects(require('../publish-android.cjs')({}), /Standalone Android publication is disabled/);
});

test('Android is packaged only after build and emulator tests, for the unified publisher', ()=>{
  const workflow = fs.readFileSync(path.join(__dirname,'../../.github/workflows/android-client.yml'),'utf8');
  assert.match(workflow,/workflow_call:/);
  assert.match(workflow,/needs: \\[build, device\\]/);
  assert.match(workflow,/name: release-android/);
  assert.doesNotMatch(workflow,/contents: write|publish-android/);
  assert.match(workflow,/inputs.build_number \\|\\| github.run_number/);
});
