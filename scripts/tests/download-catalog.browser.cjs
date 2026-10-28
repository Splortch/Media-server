const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const os = require('node:os');
const assert = require('node:assert/strict');
const {chromium} = require('../../frontend/node_modules/@playwright/test');
const AxeBuilder = require('../../frontend/node_modules/@axe-core/playwright').default;
const {build} = require('../build-download-catalog.cjs');
const {fixtures} = require('./download-catalog.fixtures.cjs');
async function host(directory) {
  const server = http.createServer((request,response) => {
    const filename = request.url === '/' ? 'index.html' : request.url.slice(1);
    if (!['index.html','styles.css','app.js'].includes(filename)) { response.writeHead(404);response.end();return; }
    response.setHeader('Content-Type', filename.endsWith('.css') ? 'text/css' : filename.endsWith('.js') ? 'application/javascript' : 'text/html');
    response.end(fs.readFileSync(path.join(directory,filename)));
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  return {url:`http://127.0.0.1:${server.address().port}`, close:()=>new Promise(resolve=>server.close(resolve))};
}
async function browser() { return chromium.launch({headless:true, ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE ? {executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE} : {})}); }
if (process.argv[2] === '--capture') {
  (async()=>{
    const server = await host(path.resolve(process.argv[3])); let instance;
    try {
      instance = await browser();
      for (const [name,width,height] of [['desktop',1440,1150],['mobile',390,844]]) {
        const page = await instance.newPage({viewport:{width,height}});
        await page.goto(server.url);
        await page.screenshot({path:`artifacts/downloads-${name}.png`,fullPage:true});
        await page.close();
      }
    } finally { await instance?.close(); await server.close(); }
  })().catch(error=>{console.error(error);process.exitCode=1;});
} else {
  const {test} = require('node:test');
  test('desktop/mobile categories keep older releases collapsed and preserve keyboard navigation', async()=>{
    const directory = fs.mkdtempSync(path.join(os.tmpdir(),'catalog-browser-'));
    build({repository:'Splortch/Media-server',releases:fixtures(),output:directory});
    const server = await host(directory); let instance;
    try {
      instance = await browser();
      for (const [width,height] of [[1440,1000],[390,844]]) {
        const page = await instance.newPage({viewport:{width,height}});
        await page.goto(server.url);
        assert.equal(await page.locator('.platform-card:visible').count(),5);
        const windows = page.locator('[data-platform="server-windows"]');
        assert.equal(await windows.locator('.history ul').isVisible(),false);
        await windows.locator('summary').focus(); await page.keyboard.press('Enter');
        assert.equal(await windows.locator('.history ul').isVisible(),true);
        assert.equal(await page.locator('[data-platform="client-android"] .history ul').isVisible(),false);
        await page.getByRole('button',{name:/^Clients/}).click();
        assert.equal(await page.locator('.platform-card:visible').count(),2);
        assert.equal(await page.locator('#servers').isVisible(),false);
        await page.getByRole('button',{name:/^Servers/}).click();
        assert.equal(await page.locator('.platform-card:visible').count(),3);
        await page.getByRole('button',{name:/^All platforms/}).click();
        assert.equal(await page.locator('.platform-card:visible').count(),5);
        assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),true);
        assert.deepEqual((await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze()).violations,[]);
        await page.close();
      }
    } finally { await instance?.close(); await server.close(); fs.rmSync(directory,{recursive:true}); }
  });
}
