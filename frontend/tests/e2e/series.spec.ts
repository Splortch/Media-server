import { test, expect } from '@playwright/test';
import { mkdir, copyFile, readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';

test('identified seasons remain information-first through keyboard selection and playback', async ({
  page,
}) => {
  test.setTimeout(120000);
  const media = process.env.E2E_MEDIA!;
  const data = process.env.E2E_DATA!;
  expect(media).toBeTruthy();
  expect(data).toBeTruthy();
  const csrf = async () => ({
    'X-CSRF-TOKEN': (await (await page.request.get('/api/auth/csrf')).json())
      .token,
  });
  if ((await (await page.request.get('/api/setup')).json()).required) {
    const setup = await page.request.post('/api/setup', {
      headers: await csrf(),
      data: {
        name: 'maintainer',
        password: 'test-only-long-password',
        token: (await readFile(path.join(data, 'setup-token'), 'utf8')).trim(),
      },
    });
    expect(setup.status()).toBe(201);
  }
  expect(
    (
      await page.request.post('/api/auth/login', {
        headers: await csrf(),
        data: { name: 'maintainer', password: 'test-only-long-password' },
      })
    ).ok(),
  ).toBeTruthy();
  const folder = path.join(media, 'series-fixture');
  await mkdir(folder, { recursive: true });
  // Other suites rename/remove their media. Own this fixture completely.
  const firstFile = path.join(folder, 'one.webm');
  execFileSync('ffmpeg', [
    '-v',
    'error',
    '-f',
    'lavfi',
    '-i',
    'testsrc=size=320x180:rate=24',
    '-f',
    'lavfi',
    '-i',
    'sine=frequency=440',
    '-t',
    '4',
    '-c:v',
    'libvpx-vp9',
    '-c:a',
    'libopus',
    '-y',
    firstFile,
  ]);
  await copyFile(firstFile, path.join(folder, 'two.webm'));
  const created = await page.request.post('/api/libraries', {
    headers: await csrf(),
    data: { name: 'Series fixture', path: folder, kind: 'tv' },
  });
  expect(created.status()).toBe(201);
  const library = await created.json();
  const scan = await page.request.post(`/api/libraries/${library.id}/scan`, {
    headers: await csrf(),
  });
  expect(scan.status()).toBe(202);
  const job = await scan.json();
  await expect
    .poll(
      async () =>
        (await (await page.request.get(`/api/scans/${job.id}`)).json()).state,
    )
    .toBe('completed');
  const catalog = await (
    await page.request.get(`/api/media-page?libraryId=${library.id}`)
  ).json();
  expect(catalog.items).toHaveLength(2);
  const titles: number[] = [];
  for (const [index, item] of [...catalog.items]
    .sort((a, b) => a.id - b.id)
    .entries()) {
    titles.push(item.id);
    const saved = await page.request.put(`/api/media/${item.id}/metadata`, {
      headers: await csrf(),
      data: {
        expectedRevision: 0,
        details: {
          title: `Series fixture: ${index === 0 ? 'Arrival' : 'Departure'}`,
          year: 2020,
          synopsis: 'A synthetic episode for information-first navigation.',
          genres: ['Drama'],
          cast: ['Fixture actor'],
          series: 'Series fixture',
          season: index + 1,
          episode: 1,
          provider: 'manual',
          providerId: null,
          attribution: 'Manual metadata',
        },
      },
    });
    expect(saved.ok()).toBeTruthy();
  }
  const streams: string[] = [];
  page.on('request', (request) => {
    if (/\/stream(?:\?|$)|\/playback-sessions/.test(request.url()))
      streams.push(request.url());
  });
  const grouped = await (
    await page.request.get('/api/catalog?prefix=Series%20fixture')
  ).json();
  expect(grouped.items).toHaveLength(1);
  expect(grouped.items[0].episodeCount).toBe(2);
  await page.goto('/#library');
  await page.getByLabel('Search library').fill('Series fixture');
  const first = page.locator(`[data-media-id="${titles[0]}"]`);
  await expect(first.locator('img')).toBeVisible({ timeout: 45000 });
  await expect
    .poll(() =>
      first
        .locator('img')
        .evaluate((img: HTMLImageElement) => img.naturalWidth),
    )
    .toBeGreaterThan(0);
  await first.focus();
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('heading', { name: 'Series fixture', exact: true }),
  ).toBeFocused();
  await expect(
    page.getByRole('button', { name: 'Play from beginning' }),
  ).toHaveCount(0);
  await expect(page.locator('video')).toHaveCount(0);
  expect(streams).toEqual([]);
  await page
    .getByRole('combobox', { name: 'Season', exact: true })
    .selectOption('2');
  await expect(
    page.getByRole('button', {
      name: /^Episode 1 · Series fixture: Departure/,
    }),
  ).toBeEnabled();
  await page
    .getByRole('combobox', { name: 'Season', exact: true })
    .selectOption('1');
  await page
    .getByRole('button', { name: /^Episode 1 · Series fixture: Arrival/ })
    .click();
  const arrival = page.getByRole('heading', {
    name: 'Series fixture: Arrival',
    exact: true,
  });
  await expect(arrival).toBeFocused();
  await expect(page.locator('video')).toHaveCount(0);
  expect(streams).toEqual([]);
  // An admin can upload artwork independently of metadata-provider configuration.
  const poster = path.join(folder, 'replacement.png');
  execFileSync('ffmpeg', [
    '-v',
    'error',
    '-f',
    'lavfi',
    '-i',
    'color=c=teal:size=300x450',
    '-frames:v',
    '1',
    '-threads',
    '1',
    '-y',
    poster,
  ]);
  await page.getByText('Manage artwork', { exact: true }).click();
  await page.getByLabel('Upload artwork').setInputFiles(poster);
  await expect(
    page.getByText('Artwork updated.', { exact: true }),
  ).toBeVisible();
  await expect(page.locator('.metadata-poster')).toBeVisible();
  await page.getByText('Manage artwork', { exact: true }).click();
  await page.getByRole('button', { name: 'Mark watched', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm change' }).click();
  await expect(
    page.getByText('Marked watched.', { exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Undo watch state change' }).click();
  await expect(
    page.getByText('Previous watch state restored.', { exact: true }),
  ).toBeVisible();
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({
    path: 'test-results/visual/series-title-information.png',
    fullPage: true,
  });
  await page.getByRole('button', { name: 'Play from beginning' }).click();
  const video = page.getByLabel('Series fixture: Arrival', { exact: true });
  await expect(video).toBeVisible();
  await expect.poll(() => streams.length).toBeGreaterThan(0);
  await page.getByRole('slider', { name: 'Seek', exact: true }).focus();
  await expect(page.locator('.seek-preview img')).toBeVisible({
    timeout: 45000,
  });
  await expect
    .poll(() =>
      page
        .locator('.seek-preview img')
        .evaluate((img: HTMLImageElement) => img.naturalWidth),
    )
    .toBeGreaterThan(0);
  const auto = page.getByLabel('Automatically play the next episode');
  await expect(auto).not.toBeChecked();
  await auto.check();
  await page.getByRole('button', { name: 'Fullscreen', exact: true }).click();
  await expect
    .poll(() => page.evaluate(() => !!document.fullscreenElement))
    .toBe(true);
  await expect(page.getByText(/Next episode starts in/)).toBeVisible({
    timeout: 15000,
  });
  await page.getByRole('button', { name: 'Cancel next episode' }).click();
  await expect(page.getByText(/Next episode starts in/)).toHaveCount(0);
  await expect(video).toBeVisible();
  await page
    .getByRole('button', { name: 'Play next episode', exact: true })
    .click();
  const departure = page.getByLabel('Series fixture: Departure', {
    exact: true,
  });
  await expect(departure).toBeVisible();
  await page.getByRole('button', { name: 'Back to title information' }).click();
  await expect(
    page.getByRole('heading', {
      name: 'Series fixture: Departure',
      exact: true,
    }),
  ).toBeFocused();
  await expect(page.locator('video')).toHaveCount(0);
  await page
    .getByRole('button', { name: 'Remove from Continue Watching' })
    .click();
  await page.getByRole('button', { name: 'Confirm change' }).click();
  await expect(
    page.getByText('Removed from Continue Watching. Resume position kept.'),
  ).toBeVisible();
  const watching = await (
    await page.request.get('/api/v1/continue-watching')
  ).json();
  expect(
    watching.some((x: { mediaItemId: number }) => x.mediaItemId === titles[1]),
  ).toBe(false);
  await page.getByRole('link', { name: 'Back to collection' }).click();
  await expect(page.getByLabel('Search library')).toHaveValue('Series fixture');
  await expect(first).toBeFocused();
  await expect(first.locator('img')).toHaveAttribute('src', /artwork\/poster/);
  await page.screenshot({
    path: 'test-results/visual/grouped-library-posters.png',
    fullPage: true,
  });
});
