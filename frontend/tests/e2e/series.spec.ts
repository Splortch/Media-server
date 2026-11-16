import { test, expect } from '@playwright/test';
import { mkdir, copyFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';

test('identified seasons remain information-first through keyboard selection and playback', async ({
  page,
}) => {
  test.setTimeout(90000);
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
  await Promise.all(
    ['one', 'two'].map((name) =>
      copyFile(
        path.join(media, 'sample.webm'),
        path.join(folder, name + '.webm'),
      ),
    ),
  );
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
  for (const [index, item] of catalog.items.entries()) {
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
  await page.goto('/#library');
  await page.getByLabel('Search library').fill('Series fixture');
  const first = page.locator(`[data-media-id="${titles[0]}"]`);
  await first.focus();
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('heading', { name: 'Series fixture: Arrival', exact: true }),
  ).toBeFocused();
  await expect(
    page.getByRole('region', { name: 'Seasons and episodes' }),
  ).toBeVisible();
  await expect(
    page.getByRole('combobox', { name: 'Season', exact: true }),
  ).toHaveValue('1');
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
  await page.getByRole('button', { name: /^View next episode/ }).focus();
  await page.keyboard.press('Enter');
  const secondHeading = page.getByRole('heading', {
    name: 'Series fixture: Departure',
    exact: true,
  });
  await expect(secondHeading).toBeFocused();
  await expect(
    page.getByRole('combobox', { name: 'Season', exact: true }),
  ).toHaveValue('2');
  await expect(page.locator('video')).toHaveCount(0);
  expect(streams).toEqual([]);
  await expect(
    page.getByRole('button', { name: /^View next episode/ }),
  ).toHaveCount(0);
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
  const video = page.getByLabel('Series fixture: Departure', { exact: true });
  await expect(video).toBeVisible();
  await expect.poll(() => streams.length).toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Back to title information' }).click();
  await expect(secondHeading).toBeFocused();
  await expect(page.locator('video')).toHaveCount(0);
  await page.getByRole('link', { name: 'Back to collection' }).click();
  await expect(page.getByLabel('Search library')).toHaveValue('Series fixture');
  await expect(first).toBeFocused();
});
