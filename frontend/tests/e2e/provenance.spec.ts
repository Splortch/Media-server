import { test, expect } from '@playwright/test';
import { mkdir, readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';

test('library explanations and wrong-match review keep files private and unchanged', async ({
  page,
}) => {
  test.setTimeout(120000);
  const media = process.env.E2E_MEDIA!;
  const data = process.env.E2E_DATA!;
  const csrf = async () => ({
    'X-CSRF-TOKEN': (await (await page.request.get('/api/auth/csrf')).json())
      .token,
  });
  if ((await (await page.request.get('/api/setup')).json()).required) {
    expect(
      (
        await page.request.post('/api/setup', {
          headers: await csrf(),
          data: {
            name: 'maintainer',
            password: 'test-only-long-password',
            token: (
              await readFile(path.join(data, 'setup-token'), 'utf8')
            ).trim(),
          },
        })
      ).status(),
    ).toBe(201);
  }
  expect(
    (
      await page.request.post('/api/auth/login', {
        headers: await csrf(),
        data: { name: 'maintainer', password: 'test-only-long-password' },
      })
    ).ok(),
  ).toBeTruthy();
  const folder = path.join(media, 'provenance-fixture');
  await mkdir(folder, { recursive: true });
  const filename = path.join(folder, 'Provenance fixture.webm');
  execFileSync('ffmpeg', [
    '-v',
    'error',
    '-f',
    'lavfi',
    '-i',
    'testsrc=size=160x90:rate=12',
    '-t',
    '2',
    '-c:v',
    'libvpx-vp9',
    '-threads',
    '1',
    '-y',
    filename,
  ]);
  const original = await readFile(filename);
  const library = await page.request.post('/api/libraries', {
    headers: await csrf(),
    data: { name: 'Provenance library', path: folder },
  });
  expect(library.status()).toBe(201);
  const libraryId = (await library.json()).id;
  const scan = await page.request.post(`/api/libraries/${libraryId}/scan`, {
    headers: await csrf(),
  });
  const jobId = (await scan.json()).id;
  await expect
    .poll(
      async () =>
        (await (await page.request.get(`/api/scans/${jobId}`)).json()).state,
    )
    .toBe('completed');
  const entries = await (
    await page.request.get('/api/catalog?prefix=Provenance%20fixture')
  ).json();
  const id = entries.items[0].id;
  const publicSource = await (
    await page.request.get(`/api/media/${id}/provenance`)
  ).json();
  expect(JSON.stringify(publicSource)).not.toContain(folder);
  expect(publicSource.identification).toBe('Filename-derived title');
  expect(publicSource.addedAt).toBeTruthy();
  expect(publicSource.lastScannedAt).toBeTruthy();
  await page.goto(`/#media/${id}`);
  await page.getByText('Why is this here?', { exact: true }).click();
  await expect(
    page.getByText('Filename-derived title', { exact: true }),
  ).toBeVisible();
  await expect(page.getByText(filename, { exact: true })).toHaveCount(0);
  await page.getByLabel('What looks wrong?').selectOption('title');
  await page
    .getByLabel('Optional note (500 characters)')
    .fill('Please check this synthetic fixture.');
  await page
    .getByRole('button', { name: 'Report wrong match', exact: true })
    .click();
  await expect(
    page.getByText('Your title report: Open.', { exact: true }),
  ).toBeVisible();
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page.getByText('Metadata match reports', { exact: true }).click();
  const report = page.getByRole('article').filter({
    has: page.getByRole('heading', {
      name: 'Provenance fixture',
      exact: true,
    }),
  });
  await expect(
    report.getByText(/Please check this synthetic fixture/),
  ).toBeVisible();
  await report
    .getByText('Administrator investigation', { exact: true })
    .click();
  await expect(report.getByText(filename, { exact: true })).toBeVisible();
  await report
    .getByRole('link', { name: 'Open title to correct metadata' })
    .click();
  await page.getByText('Why is this here?', { exact: true }).click();
  await page.getByText('Administrator investigation', { exact: true }).click();
  await page
    .getByRole('button', { name: "Correct this title's metadata" })
    .click();
  await expect(page.locator('.metadata-editor')).toHaveAttribute('open', '');
  await page
    .getByRole('button', { name: 'Resolve report', exact: true })
    .click();
  await expect(
    page.getByText('No open reports for this title.', { exact: true }),
  ).toBeVisible();
  expect((await readFile(filename)).equals(original)).toBe(true);
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({
    path: 'test-results/visual/library-provenance-report.png',
    fullPage: true,
  });
});
