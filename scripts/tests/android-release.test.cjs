const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
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
