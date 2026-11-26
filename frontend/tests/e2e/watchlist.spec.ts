import { test, expect } from '@playwright/test';
import { copyFile, mkdir, readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';

test('private watchlist works with directional focus and touch on a narrow screen', async ({
  page,
  browser,
}) => {
  test.setTimeout(90000);
  const data = process.env.E2E_DATA!;
  const media = path.join(process.env.E2E_MEDIA!, 'watchlist-fixtures');
  const login = { name: 'maintainer', password: 'test-only-long-password' };
  const csrf = async () => ({
    'X-CSRF-TOKEN': (await (await page.request.get('/api/auth/csrf')).json())
      .token,
  });
  if ((await (await page.request.get('/api/setup')).json()).required) {
    const setup = await page.request.post('/api/setup', {
      headers: await csrf(),
      data: {
        ...login,
        token: (await readFile(path.join(data, 'setup-token'), 'utf8')).trim(),
      },
    });
    expect(setup.status()).toBe(201);
  }
  expect(
    (
      await page.request.post('/api/auth/login', {
        headers: await csrf(),
        data: login,
      })
    ).ok(),
  ).toBeTruthy();

  await mkdir(media);
  const seed = path.join(data, 'watchlist-fixture.webm');
  execFileSync('ffmpeg', [
    '-v',
    'error',
    '-i',
    path.resolve('../tests/fixtures/download.webm'),
    '-map_metadata',
    '-1',
    '-c',
    'copy',
    '-y',
    seed,
  ]);
  await Promise.all(
    ['Watchlist A', 'Watchlist B', 'Watchlist C', 'Watchlist D'].map((title) =>
      copyFile(seed, path.join(media, title + '.webm')),
    ),
  );
  await page.goto('/#settings');
  await page.getByLabel('Library name').fill('Watchlist fixtures');
  await page.getByLabel('Directory on the server').fill(media);
  await page.getByRole('button', { name: 'Add library', exact: true }).click();
  await page
    .getByRole('button', { name: 'Scan library Watchlist fixtures' })
    .click();
  await expect(
    page.getByRole('status').filter({ hasText: 'completed' }),
  ).toBeVisible({ timeout: 30000 });
  const shortcuts = page.getByRole('combobox', {
    name: 'Playback keyboard shortcuts',
  });
  await shortcuts.selectOption('off');
  await page.reload();
  await expect(shortcuts).toHaveValue('off');
  await shortcuts.selectOption('standard');

  await page.getByRole('link', { name: 'Library', exact: true }).click();
  await page.getByLabel('Search library').fill('Watchlist');
  const cards = page.locator('.media-grid > .media-card');
  await expect(cards).toHaveCount(4);
  await page.setViewportSize({ width: 390, height: 844 });
  await cards.nth(0).focus();
  await page.keyboard.press('ArrowRight');
  await expect(cards.nth(1)).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(cards.nth(3)).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await expect(cards.nth(2)).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(cards.nth(0)).toBeFocused();
  await page.keyboard.press('Enter');
  const add = page.getByRole('button', {
    name: 'Add to watchlist',
    exact: true,
  });
  await add.click();
  const remove = page.getByRole('button', {
    name: 'Remove from watchlist',
    exact: true,
  });
  await expect(remove).toBeFocused();
  await page.getByRole('link', { name: 'Home', exact: true }).click();
  const recommendations = page.getByRole('region', {
    name: 'Recommended for you',
  });
  await expect(recommendations).toContainText(
    'From the same library as Watchlist A',
  );
  await expect(
    recommendations.getByRole('link', { name: 'Watchlist B', exact: true }),
  ).toBeVisible();

  const touch = await browser.newContext({
    baseURL: 'http://127.0.0.1:5173',
    storageState: await page.context().storageState(),
    viewport: { width: 390, height: 844 },
    hasTouch: true,
  });
  try {
    const mobile = await touch.newPage();
    await mobile.goto('/');
    await mobile.getByRole('link', { name: 'Watchlist', exact: true }).tap();
    await expect(
      mobile.getByRole('heading', { name: 'Watchlist', exact: true }),
    ).toBeVisible();
    const axe = await new AxeBuilder({ page: mobile })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    expect(axe.violations).toEqual([]);
    await mobile
      .getByRole('heading', { name: 'Watchlist A', exact: true })
      .tap();
    await mobile
      .getByRole('button', { name: 'Remove from watchlist', exact: true })
      .tap();
    await expect(
      mobile.getByRole('button', { name: 'Add to watchlist', exact: true }),
    ).toBeVisible();
    await mobile
      .getByRole('link', { name: 'Back to collection', exact: true })
      .tap();
    await expect(
      mobile.getByText('Open a title to add it to your watchlist.'),
    ).toBeVisible();
  } finally {
    await touch.close();
  }
});
