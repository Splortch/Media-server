import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
test('setup, login, add directory, scan, stream ranges and persist resume', async ({
  page,
}) => {
  const data = process.env.E2E_DATA!;
  const media = process.env.E2E_MEDIA!;
  expect(
    data,
    'E2E_DATA must point to a fresh server data directory',
  ).toBeTruthy();
  expect(media).toBeTruthy();
  await page.goto('/');
  await expect(
    page.getByRole('heading', { name: 'Set up your server' }),
  ).toBeVisible();
  await page
    .getByLabel('Setup token')
    .fill((await readFile(path.join(data, 'setup-token'), 'utf8')).trim());
  await page.getByLabel('Username').fill('maintainer');
  await page
    .getByLabel('Password', { exact: true })
    .fill('test-only-long-password');
  await page.getByRole('button', { name: 'Create administrator' }).click();
  await expect(
    page.getByRole('heading', { name: 'Sign in', exact: true }),
  ).toBeVisible();
  await page.getByLabel('Username').fill('maintainer');
  await page
    .getByLabel('Password', { exact: true })
    .fill('test-only-long-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await page.getByLabel('Library name').fill('Test collection');
  await page.getByLabel('Directory on the server').fill(media);
  await page.getByRole('button', { name: 'Add library', exact: true }).click();
  await page.getByRole('button', { name: 'Scan library' }).click();
  await expect(page.getByRole('status')).toContainText('completed', {
    timeout: 30000,
  });
  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'sample', exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: '/tmp/media-library-desktop.png',
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: '/tmp/media-library-mobile.png',
    fullPage: true,
  });
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.getByRole('heading', { name: 'sample', exact: true }).click();
  const video = page.getByLabel('sample', { exact: true });
  await expect(video).toBeVisible();
  await expect
    .poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState))
    .toBeGreaterThanOrEqual(1);
  await video.evaluate(async (v: HTMLVideoElement) => {
    v.muted = true;
    await v.play();
  });
  await expect
    .poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime))
    .toBeGreaterThan(0);
  await video.evaluate((v: HTMLVideoElement) => {
    v.currentTime = 3;
    v.pause();
  });
  await expect
    .poll(async () => {
      const response = await page.request.get('/api/progress');
      const values = await response.json();
      return values[0]?.seconds ?? 0;
    })
    .toBeGreaterThanOrEqual(3);
  const source = await video.getAttribute('src');
  const range = await page.request.get(source!, {
    headers: { Range: 'bytes=0-15' },
  });
  expect(range.status()).toBe(206);
  expect((await range.body()).length).toBe(16);
  await page.reload();
  await expect
    .poll(() =>
      page
        .getByLabel('sample', { exact: true })
        .evaluate((v: HTMLVideoElement) => v.currentTime),
    )
    .toBeGreaterThanOrEqual(3);
});
